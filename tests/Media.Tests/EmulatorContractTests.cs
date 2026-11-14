using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using Amazon.S3;
using Amazon.S3.Model;

namespace NewStore.Media.Tests;

/// <summary>
/// What this phase relies on in S3, checked against the emulator. A Floci upgrade that changes one of these fails here
/// (ADR 0018). Each test makes its own bucket and talks to it the way a browser will: a plain HttpClient, no AWS credentials.
/// </summary>
public sealed partial class EmulatorContractTests(S3Server s3)
{
    private const int MiB = 1024 * 1024;
    private const string Origin = "http://localhost:8100";

    // Redirects off, so a test sees S3's own answer; cookies off, so nothing is sent that a browser would not send here.
    private static readonly HttpClient Browser = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A bucket with the CORS rules, the public access block and the policy from Global Constraints.</summary>
    private async Task<string> CreateConfiguredBucketAsync()
    {
        var bucket = await s3.CreateBucketAsync("contract", Ct);
        using var client = s3.CreateClient(s3.InternalUrl);

        await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = ["http://localhost:8100", "http://localhost:4200", "http://localhost:5100"],
                        AllowedMethods = ["PUT", "GET", "HEAD"],
                        AllowedHeaders = ["*"],
                        ExposeHeaders = ["ETag"],
                        MaxAgeSeconds = 3000,
                    },
                ],
            },
        }, Ct);

        await client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = false,
                RestrictPublicBuckets = false,
            },
        }, Ct);

        await client.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = HlsReadPolicy(bucket) }, Ct);

        return bucket;
    }

    /// <summary>The policy from Global Constraints: anyone may read <c>hls/*</c>, nothing else.</summary>
    private static string HlsReadPolicy(string bucket) => JsonSerializer.Serialize(new
    {
        Version = "2012-10-17",
        Statement = new[]
        {
            new
            {
                Effect = "Allow",
                Principal = "*",
                Action = "s3:GetObject",
                Resource = $"arn:aws:s3:::{bucket}/hls/*",
            },
        },
    });

    private static string PartUrl(IAmazonS3 signer, string bucket, string key, string uploadId, int partNumber,
        TimeSpan? lifetime = null, long? contentLength = null)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            UploadId = uploadId,
            PartNumber = partNumber,
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            Protocol = Protocol.HTTP, // "Defaults to https"
        };
        if (contentLength is not null)
        {
            request.Headers.ContentLength = contentLength.Value;
        }

        return signer.GetPreSignedURL(request);
    }

    private static async Task<HttpResponseMessage> PutAsync(string url, byte[] body, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(body) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await Browser.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, Action<HttpRequestHeaders>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        headers?.Invoke(request.Headers);
        return await Browser.SendAsync(request, Ct);
    }

    /// <summary>Asserts the status and the S3 error code in the body, so a test fails for the reason it names, not for any 403.</summary>
    private static async Task AssertS3ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, ErrorCode().Match(body).Groups[1].Value);
    }

    [GeneratedRegex("<Code>([^<]*)</Code>")]
    private static partial Regex ErrorCode();

    private static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static string Etag(HttpResponseMessage response) => response.Headers.ETag?.Tag
        ?? throw new InvalidOperationException("The response has no ETag.");

    /// <summary>The URL with its host swapped, the way a wrong host reaches the server.</summary>
    private static string WithHost(string url, string host) => new UriBuilder(url) { Host = host }.Uri.ToString();

    private static async Task<string> StartUploadAsync(IAmazonS3 client, string bucket, string key)
    {
        var started = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key }, Ct);
        return started.UploadId;
    }

    [Fact]
    public async Task Part_urls_signed_for_the_public_host_upload_a_multipart_file()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl); // never sends a request
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);

        var sizes = new[] { 5 * MiB, 5 * MiB, 2 * MiB };
        var parts = new List<PartETag>();
        for (var i = 0; i < sizes.Length; i++)
        {
            var url = PartUrl(signer, bucket, key, uploadId, i + 1);
            Assert.Equal("localhost", new Uri(url).Host);
            using var response = await PutAsync(url, RandomBytes(sizes[i]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            parts.Add(new PartETag(i + 1, Etag(response)));
        }

        await server.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = parts,
        }, Ct);

        var head = await server.GetObjectMetadataAsync(bucket, key, Ct);
        Assert.Equal(12L * MiB, head.ContentLength);
    }

    [Fact]
    public async Task Url_signed_for_another_host_is_refused()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);

        var signedFor127 = PartUrl(server, bucket, key, uploadId, 1);

        // Control: the same URL, rebuilt through Uri, still works on the host it was signed for.
        using var control = await PutAsync(WithHost(signedFor127, "127.0.0.1"), RandomBytes(5 * MiB));
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);

        using var response = await PutAsync(WithHost(signedFor127, "localhost"), RandomBytes(5 * MiB));
        await AssertS3ErrorAsync(response, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
    }

    [Fact]
    public async Task Expired_url_is_refused()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);

        var url = PartUrl(signer, bucket, key, uploadId, 1, lifetime: TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        using var response = await PutAsync(url, RandomBytes(5 * MiB));

        await AssertS3ErrorAsync(response, HttpStatusCode.Forbidden, "AccessDenied");
        Assert.Contains("expired", await response.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cors_preflight_allows_a_part_put_and_exposes_the_etag()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);
        var url = PartUrl(signer, bucket, key, uploadId, 1);

        async Task<HttpResponseMessage> PreflightAsync(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, url);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "PUT");
            return await Browser.SendAsync(request, Ct);
        }

        using (var allowed = await PreflightAsync(Origin))
        {
            Assert.True(allowed.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent, $"Preflight was {(int)allowed.StatusCode}.");
            Assert.Equal(Origin, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        // A preflight is never authenticated (floci-io/floci#2412): a URL with a zeroed signature still gets its answer.
        var zeroed = Regex.Replace(url, "X-Amz-Signature=[0-9a-f]+", "X-Amz-Signature=" + new string('0', 64));
        Assert.NotEqual(url, zeroed);
        using (var unsigned = new HttpRequestMessage(HttpMethod.Options, zeroed))
        {
            unsigned.Headers.Add("Origin", Origin);
            unsigned.Headers.Add("Access-Control-Request-Method", "PUT");
            using var answer = await Browser.SendAsync(unsigned, Ct);
            Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent, $"Preflight was {(int)answer.StatusCode}.");
        }

        using (var put = await PutAsync(url, RandomBytes(5 * MiB), Origin))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            // The browser needs both: the origin to let the script see the response, the exposed header to read the ETag.
            Assert.Equal(Origin, Assert.Single(put.Headers.GetValues("Access-Control-Allow-Origin")));
            var exposed = string.Join(',', put.Headers.GetValues("Access-Control-Expose-Headers")).Split(',', StringSplitOptions.TrimEntries);
            Assert.Contains("ETag", exposed, StringComparer.OrdinalIgnoreCase);
        }

        using var denied = await PreflightAsync("http://evil.test");
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Listed_etags_complete_the_upload()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);

        var sizes = new[] { 5 * MiB, 5 * MiB, 2 * MiB };
        var putEtags = new List<string>();
        for (var i = 0; i < sizes.Length; i++)
        {
            using var response = await PutAsync(PartUrl(signer, bucket, key, uploadId, i + 1), RandomBytes(sizes[i]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            putEtags.Add(Etag(response)); // kept only to compare: the completion below uses the listed ones
        }

        var listed = await server.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }, Ct);
        Assert.Equal([1, 2, 3], listed.Parts.Select(p => p.PartNumber!.Value));
        Assert.Equal(putEtags, listed.Parts.Select(p => p.ETag));

        await server.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [.. listed.Parts.Select(p => new PartETag(p.PartNumber!.Value, p.ETag))],
        }, Ct);

        var head = await server.GetObjectMetadataAsync(bucket, key, Ct);
        Assert.Equal(12L * MiB, head.ContentLength);
    }

    [Fact]
    public async Task Complete_with_a_wrong_etag_is_refused()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);
        using var put = await PutAsync(PartUrl(signer, bucket, key, uploadId, 1), RandomBytes(5 * MiB));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // InvalidPart: "One or more of the specified parts could not be found. The part might not have been uploaded, or the specified ETag might not have matched the uploaded part's ETag." (CompleteMultipartUpload, "Special errors")
        var refused = await Assert.ThrowsAsync<AmazonS3Exception>(() => server.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [new PartETag(1, "\"00000000000000000000000000000000\"")],
        }, Ct));
        Assert.Equal("InvalidPart", refused.ErrorCode);
    }

    [Fact]
    public async Task Signed_content_length_fixes_the_part_size()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);

        var url = PartUrl(signer, bucket, key, uploadId, 1, contentLength: 5 * MiB);
        var signedHeaders = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["X-Amz-SignedHeaders"] ?? "";

        // Found on 2026-11-14: the SDK signs content-length when the request carries it, and Floci enforces it.
        Assert.Equal(["content-length", "host"], signedHeaders.Split(';'));

        using var shorter = await PutAsync(url, RandomBytes((5 * MiB) - 1));
        await AssertS3ErrorAsync(shorter, HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        using var exact = await PutAsync(url, RandomBytes(5 * MiB));
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
    }

    [Fact]
    public async Task Anonymous_reads_only_the_hls_prefix()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        await server.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "hls/x/master.m3u8", ContentBody = "#EXTM3U\n" }, Ct);
        await server.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "originals/x/y", ContentBody = "secret" }, Ct);

        using var open = await GetAsync($"{s3.PublicUrl}{bucket}/hls/x/master.m3u8");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        using var closed = await GetAsync($"{s3.PublicUrl}{bucket}/originals/x/y");
        await AssertS3ErrorAsync(closed, HttpStatusCode.Forbidden, "AccessDenied");
    }

    [Fact]
    public async Task Range_read_is_206()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        var body = RandomBytes(1000);
        await server.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "hls/x/v0/seg_000.ts", InputStream = new MemoryStream(body) }, Ct);

        using var response = await GetAsync($"{s3.PublicUrl}{bucket}/hls/x/v0/seg_000.ts",
            h => h.Range = new RangeHeaderValue(0, 99));

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 0-99/1000", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(body[..100], await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Download_url_sets_the_file_name()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        await server.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "originals/v1/u1", ContentBody = "video" }, Ct);

        var url = signer.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = "originals/v1/u1",
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(15),
            Protocol = Protocol.HTTP,
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentDisposition = "attachment; filename=\"trip.mp4\"" },
        });
        using var response = await GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment; filename=\"trip.mp4\"", string.Join(',', response.Content.Headers.GetValues("Content-Disposition")));
    }

    [Fact]
    public async Task Abort_removes_the_upload()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/u1";
        var uploadId = await StartUploadAsync(server, bucket, key);
        using var put = await PutAsync(PartUrl(signer, bucket, key, uploadId, 1), RandomBytes(5 * MiB));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Control: before the abort the upload is there and lists its part.
        var before = await server.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }, Ct);
        Assert.Single(before.Parts);

        await server.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId }, Ct);

        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            server.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }, Ct));
        Assert.Equal("NoSuchUpload", error.ErrorCode);
    }

    [Fact]
    public async Task Public_access_block_refuses_a_public_policy()
    {
        // What Task 2's bucket setup relies on: the block is set first, and only then is the public policy accepted.
        // Here the block forbids public policies, so the same policy that CreateConfiguredBucketAsync installs is refused.
        var bucket = await s3.CreateBucketAsync("contract", Ct);
        using var server = s3.CreateClient(s3.InternalUrl);
        await server.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = true,
                RestrictPublicBuckets = false,
            },
        }, Ct);

        var refused = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            server.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = HlsReadPolicy(bucket) }, Ct));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("AccessDenied", refused.ErrorCode);
    }

    [Fact]
    public async Task New_bucket_accepts_a_public_policy_unlike_S3()
    {
        // A GAP in Floci 2.4.0, pinned so that it is noticed. On S3 a new bucket blocks public policies, so this PutBucketPolicy
        // would be refused (Block Public Access docs). Floci reports the default block but does not enforce it for a new bucket
        // ("default bucket-level enforcement remains a gap": https://github.com/floci-io/floci/blob/2.4.0/docs/services/s3.md#block-public-access).
        // When Floci closes the gap this test fails: flip it to expect AccessDenied, as Public_access_block_refuses_a_public_policy does.
        var bucket = await s3.CreateBucketAsync("contract", Ct);
        using var server = s3.CreateClient(s3.InternalUrl);

        var block = (await server.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }, Ct)).PublicAccessBlockConfiguration;
        Assert.True(block.BlockPublicPolicy); // reported as on ...

        await server.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = HlsReadPolicy(bucket) }, Ct); // ... and not enforced
    }

    [Fact(Explicit = true)]
    public async Task Floci_takes_a_1_GiB_multipart_upload()
    {
        var bucket = await CreateConfiguredBucketAsync();
        using var server = s3.CreateClient(s3.InternalUrl);
        using var signer = s3.CreateClient(s3.PublicUrl);
        const string key = "originals/v1/big";
        const int partSize = 64 * MiB, partCount = 16;
        var uploadId = await StartUploadAsync(server, bucket, key);

        var parts = new List<PartETag>();
        for (var i = 1; i <= partCount; i++)
        {
            using var response = await PutAsync(PartUrl(signer, bucket, key, uploadId, i), RandomBytes(partSize));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            parts.Add(new PartETag(i, Etag(response)));
        }

        await server.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = parts,
        }, Ct);

        var head = await server.GetObjectMetadataAsync(bucket, key, Ct);
        Assert.Equal(1024L * MiB, head.ContentLength);
    }
}
