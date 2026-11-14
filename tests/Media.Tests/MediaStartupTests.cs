using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Media.Tests;

public sealed class MediaStartupTests(MediaFixture media, StorageDownFixture storageDown) : IClassFixture<MediaFixture>, IClassFixture<StorageDownFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Media_is_ready_with_its_tables()
    {
        using var client = media.CreateClient();

        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(Ct));
        var tables = await media.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync(Ct));
        foreach (var table in new[] { "uploads", "processing_jobs", "digital_copies", "outbox_messages", "inbox_messages" })
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public void Kestrel_body_limit_is_the_default()
    {
        // The default (30,000,000 bytes) is documented on https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0
        // Nothing in Phase 2 may raise it: the bytes of a video go to S3, not through Media.
        var limits = media.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

        Assert.Equal(30_000_000, limits.MaxRequestBodySize);
    }

    [Fact]
    public async Task Seed_configures_the_bucket_and_can_run_twice()
    {
        // A bucket of its own that no StorageSetup has touched yet: the seed must make everything.
        var bucket = $"seed-{Guid.NewGuid():N}";
        await using var app = media.WithWebHostBuilder(b => b.UseSetting("Media:Storage:Bucket", bucket));

        for (var run = 0; run < 2; run++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<IDatabaseSeeder<MediaDbContext>>();
            await seeder.SeedAsync(scope.ServiceProvider.GetRequiredService<MediaDbContext>(), Ct);
        }

        using var s3 = media.S3.CreateClient(media.S3.InternalUrl);
        var cors = (await s3.GetCORSConfigurationAsync(bucket, Ct)).Configuration.Rules;
        var rule = Assert.Single(cors);
        Assert.Equal(["http://localhost:8100", "http://localhost:4200", "http://localhost:5100"], rule.AllowedOrigins);
        Assert.Equal(["PUT", "GET", "HEAD"], rule.AllowedMethods);
        Assert.Equal(["ETag"], rule.ExposeHeaders);
        Assert.Equal(["*"], rule.AllowedHeaders);
        Assert.Equal(3000, rule.MaxAgeSeconds);

        var policy = (await s3.GetBucketPolicyAsync(bucket, Ct)).Policy;
        Assert.Equal(StorageSetup.HlsReadPolicy(bucket), System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(policy).RootElement));

        // Floci reports a block on a new bucket without enforcing it (ADR 0018), so all four values are read back: the seed set them.
        var block = (await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }, Ct)).PublicAccessBlockConfiguration;
        Assert.True(block.BlockPublicAcls);
        Assert.True(block.IgnorePublicAcls);
        Assert.False(block.BlockPublicPolicy);
        Assert.False(block.RestrictPublicBuckets);
    }

    [Fact]
    public async Task Storage_down_makes_readiness_degraded()
    {
        using var client = storageDown.CreateClient();

        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", await ready.Content.ReadAsStringAsync(Ct));
    }
}
