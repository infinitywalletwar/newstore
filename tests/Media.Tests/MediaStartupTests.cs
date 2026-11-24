using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Media.Tests;

public sealed class MediaStartupTests(MediaFixture media, StorageDownFixture storageDown) : IClassFixture<MediaFixture>, IClassFixture<StorageDownFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Media_starts_without_logging_an_error()
    {
        // The fixture started the app and migrated it. Production migrates first (the *-migrate job), so a started app has no errors to log.
        _ = media.Services;
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        var errors = media.Logs.Snapshot().Where(log => log.Level >= LogLevel.Error).ToList();

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Category}: {e.Message} {e.Exception?.Message}")));
    }

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

        // Task 5 writes raw SQL against these names, so they are spelled out in the model and not left to the naming convention.
        var columns = await media.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'uploads'")
            .ToListAsync(Ct));
        Assert.Contains("s3_upload_id", columns);
        Assert.Contains("s3_aborted_at", columns);
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

    [Fact]
    public async Task Complete_with_storage_down_is_503_and_changes_nothing()
    {
        // The row is written by hand: with S3 down there is no way to create an upload. 503 and Retry-After tell the browser to send the same request again.
        var creator = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var upload = new Upload
        {
            Id = Guid.CreateVersion7(),
            VideoId = Guid.NewGuid(),
            CreatorId = creator,
            FileName = "a.mp4",
            ContentType = "video/mp4",
            SizeBytes = 5 * 1024 * 1024,
            ObjectKey = "originals/x/y",
            S3UploadId = "gone",
            PartSize = MediaFixture.PartSize,
            PartCount = 1,
            Status = UploadStatus.Uploading,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await storageDown.WithDbAsync(async db =>
        {
            db.Uploads.Add(upload);
            return await db.SaveChangesAsync(Ct);
        });
        using var client = storageDown.CreateClient();

        var response = await Api.PostCompleteAsync(client, creator, upload.Id, [(1, "\"etag\"")]);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(UploadStatus.Uploading, (await storageDown.WithDbAsync(db => db.Uploads.SingleAsync(u => u.Id == upload.Id, Ct))).Status);
    }

    private async Task<Upload> InsertUploadingRowAsync(Guid creator)
    {
        var now = DateTimeOffset.UtcNow;
        var upload = new Upload
        {
            Id = Guid.CreateVersion7(),
            VideoId = Guid.NewGuid(),
            CreatorId = creator,
            FileName = "a.mp4",
            ContentType = "video/mp4",
            SizeBytes = 5 * 1024 * 1024,
            ObjectKey = "originals/x/y",
            S3UploadId = "gone",
            PartSize = MediaFixture.PartSize,
            PartCount = 1,
            Status = UploadStatus.Uploading,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await storageDown.WithDbAsync(async db =>
        {
            db.Uploads.Add(upload);
            return await db.SaveChangesAsync(Ct);
        });
        return upload;
    }

    [Fact]
    public async Task State_with_storage_down_is_503_and_changes_nothing()
    {
        var creator = Guid.NewGuid();
        var upload = await InsertUploadingRowAsync(creator);
        var before = await storageDown.WithDbAsync(db => db.Uploads.AsNoTracking().SingleAsync(u => u.Id == upload.Id, Ct));
        using var client = storageDown.CreateClient();

        var response = await client.SendAsUserAsync(creator, HttpMethod.Get, $"/uploads/{upload.Id}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal("urn:newstore:problem:storage-unavailable", (await response.ProblemAsync()).Type);
        var after = await storageDown.WithDbAsync(db => db.Uploads.AsNoTracking().SingleAsync(u => u.Id == upload.Id, Ct));
        Assert.Equal(UploadStatus.Uploading, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task Create_with_storage_down_is_503_and_leaves_no_row()
    {
        var creator = Guid.NewGuid();
        var video = Api.NewVideo(creator);
        using var client = storageDown.CreateClient();

        var response = await client.PostCreateAsync(creator, video);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(0, await storageDown.WithDbAsync(db => db.Uploads.CountAsync(u => u.VideoId == video, Ct)));
    }

    [Fact]
    public async Task Download_file_name_is_made_safe()
    {
        // Task 6's rule: every character outside [A-Za-z0-9._-] becomes an underscore.
        var storage = media.Services.GetRequiredService<MediaStorage>();

        var url = await storage.PresignDownloadAsync("originals/x/y", "my \"trip\" é.mp4", TimeSpan.FromMinutes(15));

        var disposition = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["response-content-disposition"];
        Assert.Equal("attachment; filename=\"my__trip___.mp4\"", disposition);
    }
}
