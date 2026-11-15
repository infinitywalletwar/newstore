using Amazon.S3;
using Amazon.S3.Model;

namespace NewStore.Media.Storage;

/// <summary>The few object calls the worker and the seed make, so a test can put a failing store in front of S3.</summary>
public interface IObjectStore
{
    /// <summary>Writes the object to <paramref name="path"/>, streamed, replacing what is there.</summary>
    Task DownloadAsync(string key, string path, CancellationToken ct);

    Task UploadAsync(string key, string path, string contentType, string cacheControl, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);
}

/// <summary>The store on S3, through <see cref="MediaStorage.Client"/>.</summary>
public sealed class S3ObjectStore(MediaStorage storage) : IObjectStore
{
    public async Task DownloadAsync(string key, string path, CancellationToken ct)
    {
        using var response = await storage.Client.GetObjectAsync(storage.Bucket, key, ct);
        await response.WriteResponseStreamToFileAsync(path, append: false, ct);
    }

    public Task UploadAsync(string key, string path, string contentType, string cacheControl, CancellationToken ct)
    {
        var request = new PutObjectRequest { BucketName = storage.Bucket, Key = key, FilePath = path, ContentType = contentType };
        request.Headers.CacheControl = cacheControl;
        return storage.Client.PutObjectAsync(request, ct);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await storage.Client.GetObjectMetadataAsync(storage.Bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
