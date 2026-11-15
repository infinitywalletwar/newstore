using System.Text.Json;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Options;

namespace NewStore.Media;

/// <summary>
/// Makes the bucket what Media needs: it exists, public ACLs are blocked, anyone may read <c>hls/*</c> and nothing else, and the
/// browser may PUT parts and read their <c>ETag</c>. Every call replaces a setting with the same value, so it is safe every time.
/// </summary>
public sealed class StorageSetup(MediaStorage storage, IOptions<StorageOptions> options)
{
    public async Task EnsureAsync(CancellationToken ct)
    {
        var client = storage.Client;
        var bucket = storage.Bucket;

        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou")
        {
            // Already there: the usual case after the first run.
        }

        // The block comes first. On S3 a new bucket blocks public policies, so the policy below is refused until
        // BlockPublicPolicy is false. Floci does not enforce that default, so only S3 proves the order (ADR 0018).
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
        }, ct);

        await client.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = HlsReadPolicy(bucket) }, ct);

        await client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = [.. options.Value.CorsOrigins],
                        AllowedMethods = ["PUT", "GET", "HEAD"],
                        AllowedHeaders = ["*"],
                        // ETag is not a CORS-safelisted response header, so the browser cannot read it unless S3 exposes it.
                        ExposeHeaders = ["ETag"],
                        MaxAgeSeconds = 3000,
                    },
                ],
            },
        }, ct);
    }

    internal static string HlsReadPolicy(string bucket) => JsonSerializer.Serialize(new
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
}
