using System.Text.RegularExpressions;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Testcontainers.Floci;

namespace NewStore.Media.Tests;

/// <summary>
/// One Floci container per test assembly. Register it with <c>[assembly: AssemblyFixture(typeof(S3Server))]</c>.
/// Signature checks are on, so a presigned URL is verified as AWS would verify it.
/// </summary>
public sealed partial class S3Server : IAsyncLifetime
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

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        var port = _container.GetMappedPublicPort(4566);
        InternalUrl = new Uri($"http://127.0.0.1:{port}");
        PublicUrl = new Uri($"http://localhost:{port}");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A client on <paramref name="serviceUrl"/> (<see cref="InternalUrl"/> or <see cref="PublicUrl"/>), with path-style addressing and the test credentials.</summary>
    public IAmazonS3 CreateClient(Uri serviceUrl)
    {
        if (serviceUrl != InternalUrl && serviceUrl != PublicUrl)
        {
            throw new ArgumentException("Use InternalUrl or PublicUrl.", nameof(serviceUrl));
        }

        var config = new AmazonS3Config { ServiceURL = serviceUrl.ToString().TrimEnd('/'), ForcePathStyle = true, AuthenticationRegion = Region };
        return new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), config);
    }

    /// <summary>
    /// Creates a new, unique bucket named <c>prefix-&lt;32 hex digits&gt;</c>. A bucket name has 3 to 63 characters of lower-case
    /// letters, digits and hyphens, so the prefix is checked: up to 30 of those.
    /// </summary>
    public async Task<string> CreateBucketAsync(string prefix, CancellationToken cancellationToken)
    {
        if (!BucketPrefix().IsMatch(prefix))
        {
            throw new ArgumentException("Use 1 to 30 lower-case letters, digits and hyphens.", nameof(prefix));
        }

        var name = $"{prefix}-{Guid.NewGuid():N}";
        using var client = CreateClient(InternalUrl);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = name }, cancellationToken);
        return name;
    }

    [GeneratedRegex("^[a-z0-9-]{1,30}$")]
    private static partial Regex BucketPrefix();
}
