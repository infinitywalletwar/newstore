using System.Diagnostics;
using System.Net.Http.Json;

namespace NewStore.Media.Tests;

/// <summary>Media with the worker and the production part size (64 MiB), for the 1 GB test only.</summary>
public sealed class LargeUploadFixture(PostgresServer postgres, KafkaServer kafka, S3Server s3) : ProcessingFixture(postgres, kafka, s3)
{
    protected override long UploadPartSize => 64 * MiB;

    private const long MiB = 1024 * 1024;
}

/// <summary>
/// The whole chain on a file of at least 1 GiB: presigned multipart upload, the worker's FFmpeg, public HLS. It takes minutes, so it is
/// <c>Explicit</c>: <c>dotnet test --explicit only</c> runs it (the nightly workflow and the Mac), and an ordinary run skips it.
/// </summary>
public sealed class LargeUploadTests(LargeUploadFixture media) : IClassFixture<LargeUploadFixture>
{
    private const long OneGiB = 1_073_741_824;
    private const int Concurrency = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task A_1_GB_upload_becomes_playable()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"newstore-large-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var times = new List<string>();
            var clock = Stopwatch.StartNew();
            void Lap(string stage)
            {
                times.Add($"{stage}: {clock.Elapsed:mm\\:ss\\.f}");
                TestContext.Current.TestOutputHelper?.WriteLine(times[^1]);
                clock.Restart();
            }

            // 1. The sample: ADR 0018's command, a 240 s 1080p30 video at 36 Mbit/s.
            var path = Path.Combine(folder, "large.mp4");
            await SampleVideos.RunFfmpegAsync(["-nostdin", "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
                "-vf", "noise=alls=20:allf=t", "-t", "240", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "36M", "-maxrate", "36M",
                "-bufsize", "72M", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-shortest", path]);
            var size = new FileInfo(path).Length;
            Assert.True(size >= OneGiB, $"The sample is {size} bytes, under 1 GiB.");
            Lap("make the sample");

            // 2. Create the upload and PUT its parts, four at a time, each a stream over its slice of the file.
            using var client = media.CreateClient();
            var creator = Guid.NewGuid();
            var created = await client.PostCreateAsync(creator, Api.NewVideo(creator), size, "large.mp4", "video/mp4");
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var upload = (await created.Content.ReadFromJsonAsync<UploadResponse>(Ct))!;
            Assert.Equal(64 * Api.MiB, upload.PartSize);
            using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(10) };
            var etags = new (int PartNumber, string ETag)[upload.PartUrls.Count];
            await Parallel.ForEachAsync(upload.PartUrls, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = Ct }, async (part, ct) =>
            {
                await using var slice = new FileSlice(path, (part.PartNumber - 1) * upload.PartSize, part.Size);
                using var content = new StreamContent(slice);
                using var response = await browser.PutAsync(part.Url, content, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                etags[part.PartNumber - 1] = (part.PartNumber, response.Headers.ETag!.Tag);
            });
            Lap($"upload {upload.PartUrls.Count} parts");

            // 3. Complete, then wait for Published.
            var completed = await client.PostCompleteAsync(creator, upload.Id, etags);
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Lap("complete");
            var final = await media.WaitForFinalAsync(new SampleUpload(creator, upload.VideoId, upload.Id), TimeSpan.FromMinutes(45));
            Assert.Equal(("Published", null), (final.Status, final.FailureReason));
            Lap("processing to Published");

            // 4. Anyone reads the master playlist and the first segment of each variant.
            var prefix = ObjectKeys.HlsPrefix(upload.VideoId, upload.Id);
            using var master = await ProcessingFixture.GetAnonymousAsync(media.PublicUrl($"{prefix}/master.m3u8"));
            Assert.Equal(HttpStatusCode.OK, master.StatusCode);
            var playlist = await master.Content.ReadAsStringAsync(Ct);
            Assert.Contains("RESOLUTION=640x360", playlist);
            Assert.Contains("RESOLUTION=1280x720", playlist);
            foreach (var variant in new[] { "v0", "v1" })
            {
                using var segment = await ProcessingFixture.GetAnonymousAsync(media.PublicUrl($"{prefix}/{variant}/seg_000.ts"));
                Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
            }

            Lap("anonymous reads");

            // 5. The stage times, in the test output.
            TestContext.Current.TestOutputHelper?.WriteLine($"Stage times for {size} bytes: {string.Join("; ", times)}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>A read-only, seekable view of <c>count</c> bytes of a file from <c>offset</c>, so <see cref="StreamContent"/> sends a part without reading the file into memory.</summary>
internal sealed class FileSlice : Stream
{
    private readonly FileStream _file;
    private readonly long _offset;
    private readonly long _count;
    private long _position;

    public FileSlice(string path, long offset, long count)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        _offset = offset;
        _count = count;
        _file.Seek(offset, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _count;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _file.Read(buffer[..(int)Math.Min(buffer.Length, _count - _position)]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _count - _position)], cancellationToken);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _count + offset,
        };
        _position = Math.Clamp(target, 0, _count);
        _file.Seek(_offset + _position, SeekOrigin.Begin);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _file.Dispose();
        }

        base.Dispose(disposing);
    }
}
