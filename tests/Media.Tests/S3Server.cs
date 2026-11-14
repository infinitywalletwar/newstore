using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Testcontainers.Floci;

namespace NewStore.Media.Tests;

/// <summary>
/// One Floci container per test assembly. Register it with <c>[assembly: AssemblyFixture(typeof(S3Server))]</c>.
/// Signature checks are on, so a presigned URL is verified as AWS would verify it.
/// </summary>
public sealed class S3Server : IAsyncLifetime
{
    public const string Image = "floci/floci:2.4.0";
    public const string AccessKey = "test", SecretKey = "test", Region = "us-east-1";

    private readonly FlociContainer _container = new FlociBuilder(Image)
        .WithEnvironment("FLOCI_SERVICES_S3_ENFORCE_AUTH", "true")
        .WithEnvironment("FLOCI_SERVICES_UI_ENABLED", "false")
        .WithEnvironment("FLOCI_STORAGE_MODE", "persistent")
        .WithEnvironment("FLOCI_STORAGE_PERSISTENT_PATH", "/app/data")
        .Build();

    /// <summary>The address a test or the server-side client uses: http://127.0.0.1:&lt;mapped port&gt;.</summary>
    public Uri InternalUrl { get; private set; } = null!;

    /// <summary>The same container under another host name: http://localhost:&lt;mapped port&gt;.</summary>
    public Uri PublicUrl { get; private set; } = null!;

    /// <summary>The container's id, so a measurement can read its cgroup.</summary>
    public string ContainerId => _container.Id;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        var port = _container.GetMappedPublicPort(4566);
        InternalUrl = new Uri($"http://127.0.0.1:{port}");
        PublicUrl = new Uri($"http://localhost:{port}");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A client on <paramref name="serviceUrl"/>, with path-style addressing and the test credentials.</summary>
    // An instance method on purpose: other tasks' tests call it as s3.CreateClient(...), as the plan's interface says.
#pragma warning disable CA1822
    public IAmazonS3 CreateClient(Uri serviceUrl, AmazonS3Config? config = null)
#pragma warning restore CA1822
    {
        config ??= new AmazonS3Config();
        config.ServiceURL = serviceUrl.ToString().TrimEnd('/');
        config.ForcePathStyle = true;
        config.AuthenticationRegion = Region;
        return new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), config);
    }

    /// <summary>Creates a new, lower-case, unique bucket named <c>prefix-n-random</c>.</summary>
    public async Task<string> CreateBucketAsync(string prefix, CancellationToken cancellationToken)
    {
        // S3 bucket names: 3 to 63 characters, lower-case letters, digits and hyphens.
        var name = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        using var client = CreateClient(InternalUrl);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = name }, cancellationToken);
        return name;
    }
}
