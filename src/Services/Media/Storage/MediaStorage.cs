using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Options;

namespace NewStore.Media;

/// <summary>
/// Media's two S3 clients. SigV4 signs the <c>host</c> header, so a URL must be signed for the host the browser will use
/// (ADR 0018). <see cref="Client"/> talks to S3 from inside the network; <see cref="PublicSigner"/> only signs.
/// </summary>
public sealed class MediaStorage : IDisposable
{
    public MediaStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        Bucket = o.Bucket;
        Client = Create(o, o.ServiceUrl);
        PublicSigner = Create(o, o.PublicServiceUrl);
        _publicProtocol = new Uri(o.PublicServiceUrl).Scheme == "https" ? Protocol.HTTPS : Protocol.HTTP;
    }

    private readonly Protocol _publicProtocol;

    /// <summary>Every request Media sends goes through this client (<c>ServiceUrl</c>).</summary>
    public IAmazonS3 Client { get; }

    /// <summary>Presigns only and never sends a request (<c>PublicServiceUrl</c>).</summary>
    public IAmazonS3 PublicSigner { get; }

    public string Bucket { get; }

    /// <summary>
    /// A URL that lets the browser PUT one part. The SDK signs <c>Content-Length</c> into it and the emulator enforces it, so the
    /// part's size is fixed by the URL (ADR 0018). <c>Expires</c> comes from the real clock, because the SDK counts
    /// <c>X-Amz-Expires</c> from its own clock; the API's <c>expiresAt</c> comes from <see cref="TimeProvider"/>.
    /// </summary>
    public Task<string> PresignPartAsync(string key, string s3UploadId, int partNumber, long partLength, TimeSpan lifetime)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            UploadId = s3UploadId,
            PartNumber = partNumber,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = _publicProtocol,
        };
        request.Headers.ContentLength = partLength;
        return PublicSigner.GetPreSignedURLAsync(request);
    }

    /// <summary>A URL that downloads the original with <paramref name="fileName"/> as the saved name (used by Task 6).</summary>
    public Task<string> PresignDownloadAsync(string key, string fileName, TimeSpan lifetime)
    {
        // The header value is built from text a creator typed: keep it to printable ASCII without quote or backslash,
        // so it cannot end the quoted string or add a header parameter.
        var safe = new string([.. fileName.Select(c => c is >= ' ' and <= '~' and not '"' and not '\\' ? c : '_')]);
        return PublicSigner.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = _publicProtocol,
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentDisposition = $"attachment; filename=\"{safe}\"" },
        });
    }

    public void Dispose()
    {
        Client.Dispose();
        PublicSigner.Dispose();
    }

    private static AmazonS3Client Create(StorageOptions o, string serviceUrl)
    {
        var config = new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = o.Region };
        return o.AccessKey is null
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
    }
}
