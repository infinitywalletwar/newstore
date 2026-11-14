using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Time.Testing;

namespace NewStore.Media.Tests;

/// <summary>
/// Media on its own database and its own bucket (set up by <see cref="StorageSetup"/>, as the seed does), with a clock the test
/// moves. A class fixture: each test class gets one app. Subclasses change the settings through the virtual members.
/// </summary>
public class MediaFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : NewStoreAppFactory<Program>, IAsyncLifetime
{
    /// <summary>The part size every test uses: S3's smallest.</summary>
    public const long PartSize = 5 * 1024 * 1024;

    /// <summary>
    /// Every "now" of the service, including the expiry timer. It starts a few 100 ns ticks past a whole microsecond: PostgreSQL
    /// keeps microseconds, so a service that returns an unrounded time answers differently before and after a round trip.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks - (DateTimeOffset.UtcNow.UtcTicks % 10), TimeSpan.Zero).AddTicks(3));

    /// <summary>Catalog, as a table of videos (<see cref="FakeCatalog"/>).</summary>
    internal StubHttpHandler Catalog { get; } = FakeCatalog.Create();

    public S3Server S3 => s3;

    public string Bucket { get; private set; } = "";

    /// <summary>Which parts of Media run. Api by default, so no worker runs; Tasks 5 and 6 set another in fixtures of their own.</summary>
    protected virtual MediaRole Role => MediaRole.Api;

    protected virtual string PartUrlLifetime => "01:00:00";

    /// <summary>How often the expiry service ticks on <see cref="Clock"/>. A day keeps it out of tests that do not move the clock that far.</summary>
    protected virtual string ExpirySweepInterval => "1.00:00:00";

    /// <summary>The address Media's own requests go to. A subclass points it at a closed port.</summary>
    protected virtual Uri ServiceUrl => s3.InternalUrl;

    /// <summary>False when S3 is not reachable, so the bucket cannot be set up.</summary>
    protected virtual bool SetUpBucket => true;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Settings["ConnectionStrings:Media"] = await postgres.CreateDatabaseAsync("media", ct);
        Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        // Two fixtures in one test run must never share a consumer group.
        Settings["Messaging:ClientId"] = $"media-{GetType().Name}";
        Settings["WarmUp:Enabled"] = "false";
        Settings["Media:Role"] = Role.ToString();
        Settings["Media:Uploads:PartSize"] = PartSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Settings["Media:Uploads:PartUrlLifetime"] = PartUrlLifetime;
        Settings["Media:Uploads:ExpirySweepInterval"] = ExpirySweepInterval;

        // 3 to 63 lower-case letters, digits and hyphens: "media-", the fixture class (at most 24 characters here) and 32 hex digits.
        Bucket = $"media-{GetType().Name.ToLowerInvariant()}-{Guid.NewGuid():N}";
        Settings["Media:Storage:Bucket"] = Bucket;
        Settings["Media:Storage:ServiceUrl"] = ServiceUrl.ToString().TrimEnd('/');
        Settings["Media:Storage:PublicServiceUrl"] = s3.PublicUrl.ToString().TrimEnd('/');
        Settings["Media:Storage:AccessKey"] = S3Server.AccessKey;
        Settings["Media:Storage:SecretKey"] = S3Server.SecretKey;
        Settings["Media:Storage:CorsOrigins:0"] = "http://localhost:8100";
        Settings["Media:Storage:CorsOrigins:1"] = "http://localhost:4200";
        Settings["Media:Storage:CorsOrigins:2"] = "http://localhost:5100";

        // Media calls Catalog as client "media"; the stub answers, so the address and the secret are never used.
        Settings["Services:Catalog"] = "http://catalog.test";
        Settings["Auth:ClientId"] = "media";
        Settings["Auth:ClientSecret"] = "media";
        Settings["Auth:TokenEndpoint"] = "http://keycloak.test/token";

        await MigrateAsync<MediaDbContext>();
        if (SetUpBucket)
        {
            await Services.GetRequiredService<StorageSetup>().EnsureAsync(ct);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.UseStubHandler<CatalogClient>(Catalog);
            // The standard pipeline waits seconds between retries; a test that makes Catalog fail does not need to.
            services.ConfigureAll<HttpStandardResilienceOptions>(o =>
            {
                o.Retry.Delay = TimeSpan.Zero;
                o.Retry.UseJitter = false;
            });
        });
    }

    /// <summary>Runs <paramref name="action"/> with a database context of the app.</summary>
    public async Task<T> WithDbAsync<T>(Func<MediaDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<MediaDbContext>());
    }
}

/// <summary>Part URLs that live 2 seconds, so a test can let them run out.</summary>
public sealed class ShortLifetimeFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : MediaFixture(postgres, kafka, s3)
{
    protected override string PartUrlLifetime => "00:00:02";
}

/// <summary>Media's S3 address is a port nothing listens on.</summary>
public sealed class StorageDownFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : MediaFixture(postgres, kafka, s3)
{
    protected override Uri ServiceUrl { get; } = ClosedPort();

    protected override bool SetUpBucket => false;

    private static Uri ClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }
}
