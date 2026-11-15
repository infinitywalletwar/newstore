using System.Collections.Concurrent;
using System.Net.Http.Json;

using Amazon.S3;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Media.Storage;

namespace NewStore.Media.Tests;

/// <summary>An upload a test made through the API, as the browser would.</summary>
public sealed record SampleUpload(Guid CreatorId, Guid VideoId, Guid UploadId);

/// <summary>
/// Media with the worker (<see cref="MediaRole.All"/>) on the real clock: the worker polls every 100 ms, waits 100 to 200 ms before a
/// retry, and renews its lease as time passes. Its work directory is its own and is deleted on dispose, because a starting worker
/// empties its directory and test classes run in parallel. Every object call goes through <see cref="Store"/>.
/// </summary>
public class ProcessingFixture : MediaFixture
{
    public ProcessingFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : base(postgres, kafka, s3)
    {
        Settings["Media:Processing:WorkDirectory"] = WorkDirectory;
        Settings["Media:Processing:PollInterval"] = "00:00:00.100";
        Settings["Media:Processing:RetryPause"] = "00:00:00.100";
        Settings["Media:Processing:MaxRetryPause"] = "00:00:00.200";
    }

    // The browser: no AWS credentials, no redirects, no cookies. It reads the public HLS files too.
    private static readonly HttpClient Browser = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string WorkDirectory { get; } = NewWorkDirectory();

    /// <summary>Makes S3 fail the next downloads of a key, and counts uploads.</summary>
    internal ObjectStoreSpy Store { get; } = new();

    protected override MediaRole Role => MediaRole.All;

    public static string NewWorkDirectory() => Path.Combine(Path.GetTempPath(), $"newstore-media-work-{Guid.NewGuid():N}");

    /// <summary>The public address of an object, as a player reads it: no signature.</summary>
    public Uri PublicUrl(string key) => new(S3.PublicUrl, $"{Bucket}/{key}");

    public static Task<HttpResponseMessage> GetAnonymousAsync(Uri url) => Browser.GetAsync(url, Ct);

    /// <summary>
    /// Uploads the file at <paramref name="path"/> as a new Draft video's, the way the browser does (create, PUT each part to its
    /// presigned URL, complete). The upload is then Processing, with a Pending job. <paramref name="beforeComplete"/> runs before the
    /// job exists, so a test can arm a fault the worker's first attempt meets.
    /// </summary>
    public async Task<SampleUpload> UploadAsync(string path, string contentType, Action<SampleUpload>? beforeComplete = null)
    {
        using var client = CreateClient();
        var creator = Guid.NewGuid();
        var videoId = Api.NewVideo(creator);
        var bytes = await File.ReadAllBytesAsync(path, Ct);

        var created = await client.PostCreateAsync(creator, videoId, bytes.Length, Path.GetFileName(path), contentType);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var upload = (await created.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
        var etags = new List<(int, string)>();
        foreach (var part in upload.PartUrls)
        {
            var offset = (int)((part.PartNumber - 1) * upload.PartSize);
            using var response = await Browser.PutAsync(part.Url, new ByteArrayContent(bytes, offset, (int)part.Size), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            etags.Add((part.PartNumber, response.Headers.ETag!.Tag));
        }

        var sample = new SampleUpload(creator, videoId, upload.Id);
        beforeComplete?.Invoke(sample);
        var completed = await client.PostCompleteAsync(creator, upload.Id, etags);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        return sample;
    }

    /// <summary>Asks the API for the upload until it is Published or Failed, as the creator's page does.</summary>
    public async Task<UploadResponse> WaitForFinalAsync(SampleUpload upload, TimeSpan? timeout = null)
    {
        using var client = CreateClient();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = await client.GetAsync(upload.CreatorId, upload.UploadId);
            if (state.Status is "Published" or "Failed")
            {
                return state;
            }

            Assert.True(DateTime.UtcNow < deadline, $"The upload is still {state.Status} after {timeout ?? TimeSpan.FromSeconds(20)}.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // The worker waits in real time (its polls, retry pauses and lease renewals), so this app runs on the system clock.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(TimeProvider.System);
            services.RemoveAll<IObjectStore>();
            services.AddSingleton<IObjectStore>(sp => new SpiedObjectStore(new S3ObjectStore(sp.GetRequiredService<MediaStorage>()), Store));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(WorkDirectory))
        {
            Directory.Delete(WorkDirectory, recursive: true);
        }
    }
}

/// <summary>A job that runs over 2 s fails with <c>timed-out</c>.</summary>
public sealed class TimeoutFixture : ProcessingFixture
{
    public TimeoutFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : base(postgres, kafka, s3)
    {
        Settings["Media:Processing:JobTimeout"] = "00:00:02";
    }
}

/// <summary>
/// Media without a worker (the role is Api): a test starts worker hosts on this fixture's database and bucket itself, with
/// <see cref="StartWorkerHost"/>, and stops them when it wants to.
/// </summary>
public sealed class HandOverFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : ProcessingFixture(postgres, kafka, s3)
{
    protected override MediaRole Role => MediaRole.Api;

    /// <summary>A second app on the same database and bucket that runs the worker, with its own work directory and consumer group.</summary>
    public WorkerHost StartWorkerHost()
    {
        var directory = NewWorkDirectory();
        var host = WithWebHostBuilder(builder => builder
            .UseSetting("Media:Role", nameof(MediaRole.All))
            .UseSetting("Media:Processing:WorkDirectory", directory)
            .UseSetting("Messaging:ClientId", $"media-worker-host-{Guid.NewGuid():N}"));
        _ = host.Services;   // builds and starts the host, and with it the worker
        return new WorkerHost(host, directory);
    }
}

/// <summary>An app that runs the worker. Disposing it stops the host as a shutdown would, then deletes its work directory.</summary>
public sealed class WorkerHost(IAsyncDisposable host, string workDirectory) : IAsyncDisposable
{
    public string WorkDirectory { get; } = workDirectory;

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        if (Directory.Exists(WorkDirectory))
        {
            Directory.Delete(WorkDirectory, recursive: true);
        }
    }
}

/// <summary>
/// Downloads of an armed key fail with S3's 503 <c>SlowDown</c>, as a busy or briefly unreachable S3 answers; uploads are counted.
/// </summary>
internal sealed class ObjectStoreSpy
{
    private readonly ConcurrentDictionary<string, int> _failuresLeft = new();
    private int _uploads;

    public int Uploads => Volatile.Read(ref _uploads);

    /// <summary>The next <paramref name="times"/> downloads of <paramref name="key"/> fail.</summary>
    public void FailDownloads(string key, int times) => _failuresLeft[key] = times;

    public bool ShouldFail(string key)
    {
        while (_failuresLeft.TryGetValue(key, out var left) && left > 0)
        {
            if (_failuresLeft.TryUpdate(key, left - 1, left))
            {
                return true;
            }
        }

        return false;
    }

    public void CountUpload() => Interlocked.Increment(ref _uploads);
}

internal sealed class SpiedObjectStore(IObjectStore inner, ObjectStoreSpy spy) : IObjectStore
{
    public Task DownloadAsync(string key, string path, CancellationToken ct) =>
        spy.ShouldFail(key)
            ? throw new AmazonS3Exception("Please reduce your request rate.", Amazon.Runtime.ErrorType.Receiver, "SlowDown", "test", HttpStatusCode.ServiceUnavailable)
            : inner.DownloadAsync(key, path, ct);

    public Task UploadAsync(string key, string path, string contentType, string cacheControl, CancellationToken ct)
    {
        spy.CountUpload();
        return inner.UploadAsync(key, path, contentType, cacheControl, ct);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => inner.ExistsAsync(key, ct);
}
