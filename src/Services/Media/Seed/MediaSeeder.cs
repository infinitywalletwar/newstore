using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.Media.Processing;
using NewStore.Media.Storage;
using NewStore.ServiceDefaults.Data;

namespace NewStore.Media;

/// <summary>
/// Seeds Media, safely on every run: the bucket's settings; one small HLS sample, made with FFmpeg the first time; and a Published
/// upload for each sample video of the catalog's seed, so each of them plays the sample and can be downloaded.
/// </summary>
public sealed class MediaSeeder(
    StorageSetup storageSetup,
    MediaStorage storage,
    IObjectStore store,
    FfmpegRunner ffmpeg,
    Ffprobe ffprobe,
    HlsEncoder encoder,
    IOptions<ProcessingOptions> options,
    TimeProvider time) : IDatabaseSeeder<MediaDbContext>
{
    public const int SampleSeconds = 10;

    private const string SampleVideosFile = "sample-videos.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SeedAsync(MediaDbContext db, CancellationToken cancellationToken)
    {
        await storageSetup.EnsureAsync(cancellationToken);
        await EnsureSampleAsync(cancellationToken);
        await EnsureSampleUploadsAsync(db, cancellationToken);
    }

    /// <summary>
    /// Makes 10 s of FFmpeg's <c>testsrc2</c> picture with a <c>sine</c> tone at 1280x720, uploads it as <c>originals/sample.mp4</c>
    /// and encodes it into <c>hls/sample/</c> as the worker encodes an upload. The master playlist goes up last, so its presence
    /// means the sample is complete; a run that died halfway is redone.
    /// </summary>
    private async Task EnsureSampleAsync(CancellationToken ct)
    {
        if (await store.ExistsAsync($"{ObjectKeys.SampleHlsPrefix}/{ObjectKeys.Master}", ct))
        {
            return;
        }

        var o = options.Value;
        var directory = Path.Combine(o.WorkDirectory, $"seed-{Guid.NewGuid():N}");
        var source = Path.Combine(directory, "sample.mp4");
        var output = Path.Combine(directory, "out");
        Directory.CreateDirectory(directory);
        try
        {
            var made = await ffmpeg.RunAsync(o.FfmpegPath,
            [
                "-nostdin", "-y",
                "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate=30:duration={SampleSeconds}",
                "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={SampleSeconds}",
                "-c:v", "libx264", "-preset", o.X264Preset, "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                source,
            ], ct);
            if (made.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg could not make the sample video ({Ffprobe.Detail(made)}).");
            }

            // An original is private (only the bucket policy's hls/* is public), so no cache may keep it either.
            await store.UploadAsync(ObjectKeys.SampleOriginal, source, "video/mp4", "private, no-store", ct);
            var info = await ffprobe.ProbeAsync(source, ct);
            await encoder.EncodeAsync(source, info, output, ct);
            await encoder.ThumbnailAsync(source, info, output, ct);
            await HlsEncoder.UploadOutputAsync(store, output, ObjectKeys.SampleHlsPrefix, ct);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A Published upload of the sample for each video in <c>sample-videos.json</c> that has no upload yet.</summary>
    private async Task EnsureSampleUploadsAsync(MediaDbContext db, CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Seed", SampleVideosFile);
        await using var file = File.OpenRead(path);
        var samples = await JsonSerializer.DeserializeAsync<List<SampleVideo>>(file, JsonOptions, ct)
            ?? throw new InvalidOperationException($"{SampleVideosFile} is empty.");

        var ids = samples.Select(s => s.VideoId).ToList();
        var withUpload = await db.Uploads.Where(u => ids.Contains(u.VideoId)).Select(u => u.VideoId).Distinct().ToListAsync(ct);
        var missing = samples.Where(s => !withUpload.Contains(s.VideoId)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var size = (await storage.Client.GetObjectMetadataAsync(storage.Bucket, ObjectKeys.SampleOriginal, ct)).ContentLength;
        var now = time.GetUtcNowToMicrosecond();
        db.Uploads.AddRange(missing.Select(sample => new Upload
        {
            Id = Guid.CreateVersion7(),
            VideoId = sample.VideoId,
            CreatorId = sample.CreatorId,
            FileName = $"{sample.VideoId}.mp4",
            ContentType = "video/mp4",
            SizeBytes = size,
            ObjectKey = ObjectKeys.SampleOriginal,
            // The seed put the sample with one PUT: there was no multipart upload, so there is no S3 upload id and one part.
            S3UploadId = "",
            PartSize = size,
            PartCount = 1,
            Status = UploadStatus.Published,
            PlaylistPath = $"{ObjectKeys.SampleHlsPrefix}/{ObjectKeys.Master}",
            ThumbnailPath = $"{ObjectKeys.SampleHlsPrefix}/{ObjectKeys.Thumbnail}",
            DurationSeconds = SampleSeconds,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            PublishedAt = now,
        }));
        await db.SaveChangesAsync(ct);
    }

    private sealed record SampleVideo(Guid VideoId, Guid CreatorId);
}
