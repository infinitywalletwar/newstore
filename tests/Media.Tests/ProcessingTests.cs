using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Media;
using NewStore.Media.Processing;

namespace NewStore.Media.Tests;

/// <summary>
/// The worker end to end: each test uploads a sample through the API as the browser would, then waits for the status the creator
/// sees. FFmpeg, PostgreSQL, Kafka and S3 are all real.
/// </summary>
public sealed class ProcessingTests(
    ProcessingFixture media, TimeoutFixture shortTimeout, HandOverFixture handOver, LeaseFixture leased, SampleVideos samples)
    : IClassFixture<ProcessingFixture>, IClassFixture<TimeoutFixture>, IClassFixture<HandOverFixture>, IClassFixture<LeaseFixture>
{
    /// <summary>Long enough for the 60 s sample to be encoded on a CI runner.</summary>
    private static readonly TimeSpan LongEncode = TimeSpan.FromSeconds(120);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Uploaded_mp4_is_published_with_two_variants_and_a_thumbnail()
    {
        var upload = await media.UploadAsync(await samples.Mp4Async(), "video/mp4");

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal(("Published", null), (final.Status, final.FailureReason));
        var prefix = ObjectKeys.HlsPrefix(upload.VideoId, upload.UploadId);
        var row = await media.WithDbAsync(db => db.Uploads.AsNoTracking().SingleAsync(u => u.Id == upload.UploadId, Ct));
        Assert.Equal(($"{prefix}/master.m3u8", $"{prefix}/thumbnail.jpg", 8), (row.PlaylistPath, row.ThumbnailPath, row.DurationSeconds));
        Assert.NotNull(row.PublishedAt);
        var job = await JobAsync(media, upload);
        Assert.Equal((JobStatus.Succeeded, 1), (job.Status, job.Attempts));

        // Anyone can play it: no signature, the HLS content type, and a short cache for the playlist.
        using var master = await ProcessingFixture.GetAnonymousAsync(media.PublicUrl($"{prefix}/master.m3u8"));
        Assert.Equal(HttpStatusCode.OK, master.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", master.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=300", master.Headers.CacheControl?.ToString());
        var playlist = await master.Content.ReadAsStringAsync(Ct);
        Assert.Contains("RESOLUTION=640x360", playlist);
        Assert.Contains("RESOLUTION=1280x720", playlist);
        Assert.Contains("v0/index.m3u8", playlist);
        Assert.Contains("v1/index.m3u8", playlist);

        using var segment = await ProcessingFixture.GetAnonymousAsync(media.PublicUrl($"{prefix}/v1/seg_000.ts"));
        Assert.Equal(HttpStatusCode.OK, segment.StatusCode);
        Assert.Equal("video/mp2t", segment.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", segment.Headers.CacheControl?.ToString());

        using var thumbnail = await ProcessingFixture.GetAnonymousAsync(media.PublicUrl($"{prefix}/thumbnail.jpg"));
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType?.MediaType);

        // Catalog learns of it through exactly one event, keyed by the video, with the same paths.
        var published = Assert.Single(await PublishedEventsAsync(media, upload));
        Assert.Equal((upload.VideoId, upload.UploadId), (published.VideoId, published.UploadId));
        Assert.Equal((row.PlaylistPath, row.ThumbnailPath, 8), (published.PlaylistPath, published.ThumbnailPath, published.DurationSeconds));
    }

    [Fact]
    public async Task Video_without_audio_is_published()
    {
        var upload = await media.UploadAsync(await samples.NoAudioAsync(), "video/mp4");

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal("Published", final.Status);
        var playlist = await MasterAsync(upload);
        Assert.Contains("RESOLUTION=640x360", playlist);
        Assert.Contains("RESOLUTION=1280x720", playlist);
        Assert.DoesNotContain("mp4a", playlist);   // no AAC in CODECS: the variants are video only
    }

    [Fact]
    public async Task Portrait_video_keeps_its_aspect_ratio()
    {
        var upload = await media.UploadAsync(await samples.PortraitAsync(), "video/mp4");

        var final = await media.WaitForFinalAsync(upload);

        // 720x1280 scaled to 360 and 720 high: 202.5 and 405 wide, which scale=-2 makes even. These are the widths FFmpeg computed
        // when the plan was written (Global Constraints, "The FFmpeg commands").
        Assert.Equal("Published", final.Status);
        var playlist = await MasterAsync(upload);
        Assert.Contains("RESOLUTION=202x360", playlist);
        Assert.Contains("RESOLUTION=406x720", playlist);
    }

    [Theory]
    [InlineData("mov", "video/quicktime")]
    [InlineData("webm", "video/webm")]
    public async Task Mov_and_webm_are_published(string container, string contentType)
    {
        var source = container == "mov" ? await samples.MovAsync() : await samples.WebmAsync();
        var upload = await media.UploadAsync(source, contentType);

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal("Published", final.Status);
        Assert.Contains("RESOLUTION=1280x720", await MasterAsync(upload));
    }

    [Fact]
    public async Task Random_bytes_fail_at_once()
    {
        var upload = await media.UploadAsync(await samples.RandomBytesAsync(), "video/mp4");

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal(("Failed", "unreadable"), (final.Status, final.FailureReason));
        var job = await JobAsync(media, upload);
        Assert.Equal((JobStatus.Failed, 1), (job.Status, job.Attempts));   // permanent: no second attempt
        Assert.Contains("exit code", job.LastError);
        Assert.Empty(await PublishedEventsAsync(media, upload));
    }

    [Fact]
    public async Task Disguised_playlist_fails()
    {
        var upload = await media.UploadAsync(await samples.DisguisedPlaylistAsync(), "video/mp4");

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal("Failed", final.Status);
        Assert.True(final.FailureReason is "unreadable" or "unsupported-format", $"The reason is {final.FailureReason}.");
        Assert.Equal(1, (await JobAsync(media, upload)).Attempts);
        Assert.Empty(await PublishedEventsAsync(media, upload));
    }

    [Fact]
    public async Task Transient_storage_failure_is_retried_then_published()
    {
        var source = await samples.Mp4Async();
        var upload = await media.UploadAsync(source, "video/mp4", beforeComplete: u => media.Store.FailDownloads(OriginalOf(u), times: 1));

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal("Published", final.Status);
        var job = await JobAsync(media, upload);
        Assert.Equal((JobStatus.Succeeded, 2), (job.Status, job.Attempts));
        Assert.Contains("503 SlowDown", job.LastError);   // the first attempt's error, kept
        Assert.Single(await PublishedEventsAsync(media, upload));
    }

    [Fact]
    public async Task Three_transient_failures_fail_the_upload()
    {
        var upload = await media.UploadAsync(await samples.Mp4Async(), "video/mp4", beforeComplete: u => media.Store.FailDownloads(OriginalOf(u), times: 3));

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal(("Failed", "retries-exhausted"), (final.Status, final.FailureReason));
        var job = await JobAsync(media, upload);
        Assert.Equal((JobStatus.Failed, 3), (job.Status, job.Attempts));
        Assert.Empty(await PublishedEventsAsync(media, upload));
    }

    [Fact]
    public async Task Stopping_the_worker_releases_the_job()
    {
        var upload = await handOver.UploadAsync(await samples.LongAsync(), "video/mp4");
        var jobId = (await JobAsync(handOver, upload)).Id;

        // A worker takes the job; once FFmpeg has written its first segment, the host stops, as on SIGTERM.
        await using (var first = handOver.StartWorkerHost())
        {
            await WaitForFileAsync(Path.Combine(first.JobsDirectory, jobId.ToString(), "out", "v0", "seg_000.ts"));
        }

        var released = await JobAsync(handOver, upload);
        Assert.Equal((JobStatus.Pending, 0, null), (released.Status, released.Attempts, released.LockedBy));
        Assert.Equal("Processing", (await handOver.CreateClient().GetAsync(upload.CreatorId, upload.UploadId)).Status);

        // Another worker starts and publishes it, with one attempt in all.
        await using var second = handOver.StartWorkerHost();
        var final = await handOver.WaitForFinalAsync(upload, LongEncode);
        Assert.Equal("Published", final.Status);
        Assert.Equal((JobStatus.Succeeded, 1), ((await JobAsync(handOver, upload)).Status, (await JobAsync(handOver, upload)).Attempts));
        Assert.Single(await PublishedEventsAsync(handOver, upload));
    }

    [Fact]
    public async Task Job_over_the_timeout_fails()
    {
        var upload = await shortTimeout.UploadAsync(await samples.LongAsync(), "video/mp4");
        var jobId = (await JobAsync(shortTimeout, upload)).Id;
        shortTimeout.Clock.Advance(TimeoutFixture.PollInterval);   // the worker's next poll claims the job

        // FFmpeg has written its first segment when the 2 s pass, so the kill lands during its run.
        await WaitForFileAsync(Path.Combine(shortTimeout.JobsDirectory, jobId.ToString(), "out", "v0", "seg_000.ts"));
        shortTimeout.Clock.Advance(TimeSpan.FromSeconds(2));
        var final = await shortTimeout.WaitForFinalAsync(upload);

        Assert.Equal(("Failed", "timed-out"), (final.Status, final.FailureReason));
        Assert.Equal((JobStatus.Failed, 1), ((await JobAsync(shortTimeout, upload)).Status, (await JobAsync(shortTimeout, upload)).Attempts));
        Assert.Empty(await PublishedEventsAsync(shortTimeout, upload));
    }

    [Fact]
    public async Task Claim_after_the_last_attempt_fails_without_starting()
    {
        // The worker of attempt 3 died holding the job (an out-of-memory kill, say): Running, its lease over, no attempts left.
        var now = Api.Micro(DateTimeOffset.UtcNow);
        var upload = new SampleUpload(Guid.NewGuid(), Guid.NewGuid(), Guid.CreateVersion7());
        await media.WithDbAsync(db =>
        {
            db.Uploads.Add(new Upload
            {
                Id = upload.UploadId,
                VideoId = upload.VideoId,
                CreatorId = upload.CreatorId,
                FileName = "crash.mp4",
                ContentType = "video/mp4",
                SizeBytes = Api.MiB,
                ObjectKey = OriginalOf(upload),
                S3UploadId = "test",
                PartSize = Api.MiB,
                PartCount = 1,
                Status = UploadStatus.Processing,
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = now,
            });
            db.ProcessingJobs.Add(new ProcessingJob
            {
                Id = Guid.CreateVersion7(),
                UploadId = upload.UploadId,
                Status = JobStatus.Running,
                Attempts = 3,
                NextRunAt = now.AddMinutes(-10),
                LockedBy = "dead-worker",
                LockedUntil = now.AddSeconds(-1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            return db.SaveChangesAsync(Ct);
        });

        var final = await media.WaitForFinalAsync(upload);

        Assert.Equal(("Failed", "retries-exhausted"), (final.Status, final.FailureReason));
        var job = await JobAsync(media, upload);
        Assert.Equal((JobStatus.Failed, 4), (job.Status, job.Attempts));
        Assert.Contains("stopped without finishing", job.LastError);
        Assert.Equal(0, media.Store.Downloads(OriginalOf(upload)));   // the fourth claim ended the job without touching the file
        Assert.Empty(await PublishedEventsAsync(media, upload));
    }

    [Fact]
    public async Task Losing_the_lease_stops_the_job_without_writing()
    {
        var upload = await leased.UploadAsync(await samples.LongAsync(), "video/mp4");
        var jobId = (await JobAsync(leased, upload)).Id;
        leased.Clock.Advance(TimeoutFixture.PollInterval);   // the worker's next poll claims the job
        var jobDirectory = Path.Combine(leased.JobsDirectory, jobId.ToString());
        await WaitForFileAsync(Path.Combine(jobDirectory, "out", "v0", "seg_000.ts"));
        var uploadsBefore = leased.Store.Uploads;

        // Another worker holds the job now, as after this one's lease had run out. The next renewal, a quarter of the lease on,
        // finds the job is not ours.
        await leased.WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.LockedBy, "another-worker"), Ct));
        leased.Clock.Advance(TimeSpan.FromSeconds(31));
        await WaitUntilAsync(() => !Directory.Exists(jobDirectory));
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

        // FFmpeg was stopped before it finished: nothing went to S3. The job, the upload and the outbox are as the other worker left them.
        Assert.Equal(uploadsBefore, leased.Store.Uploads);
        var job = await JobAsync(leased, upload);
        Assert.Equal((JobStatus.Running, "another-worker", 1), (job.Status, job.LockedBy, job.Attempts));
        Assert.Equal("Processing", (await leased.CreateClient().GetAsync(upload.CreatorId, upload.UploadId)).Status);
        Assert.Empty(await PublishedEventsAsync(leased, upload));
    }

    [Fact]
    public async Task Stop_after_the_encode_still_publishes()
    {
        // The master playlist is the job's last upload. It waits until the host has begun to stop, then succeeds.
        var upload = await handOver.UploadAsync(await samples.Mp4Async(), "video/mp4",
            beforeComplete: u => handOver.Store.HoldUpload(MasterOf(u)));

        await using (handOver.StartWorkerHost())
        {
            await handOver.Store.WaitUntilHeldAsync(MasterOf(upload));
        }

        // The stop came after the work was done, so the outcome is written: a finished encode is not thrown away.
        var job = await JobAsync(handOver, upload);
        Assert.Equal((JobStatus.Succeeded, 1), (job.Status, job.Attempts));
        Assert.Equal("Published", (await handOver.CreateClient().GetAsync(upload.CreatorId, upload.UploadId)).Status);
        Assert.Single(await PublishedEventsAsync(handOver, upload));
    }

    [Fact]
    public async Task Starting_worker_empties_only_its_jobs_folder()
    {
        // The operator pointed WorkDirectory at a folder that holds other files. Only {WorkDirectory}/jobs is the worker's.
        var directory = ProcessingFixture.NewWorkDirectory();
        var jobs = Directory.CreateDirectory(Path.Combine(directory, ProcessingOptions.JobsFolder, Guid.NewGuid().ToString()));
        await File.WriteAllTextAsync(Path.Combine(jobs.FullName, "source"), "left by a dead worker", Ct);
        var foreign = Path.Combine(directory, "not-ours.txt");
        await File.WriteAllTextAsync(foreign, "keep", Ct);
        var foreignFolder = Directory.CreateDirectory(Path.Combine(directory, "other")).FullName;

        await using (handOver.StartWorkerHost(directory))
        {
            Assert.True(File.Exists(foreign));
            Assert.True(Directory.Exists(foreignFolder));
            Assert.False(Directory.Exists(jobs.FullName));
        }
    }

    private static string MasterOf(SampleUpload upload) => $"{ObjectKeys.HlsPrefix(upload.VideoId, upload.UploadId)}/{ObjectKeys.Master}";

    private static string OriginalOf(SampleUpload upload) => ObjectKeys.Original(upload.VideoId, upload.UploadId);

    private async Task<string> MasterAsync(SampleUpload upload)
    {
        using var response = await ProcessingFixture.GetAnonymousAsync(
            media.PublicUrl($"{ObjectKeys.HlsPrefix(upload.VideoId, upload.UploadId)}/master.m3u8"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static Task<ProcessingJob> JobAsync(MediaFixture app, SampleUpload upload) =>
        app.WithDbAsync(db => db.ProcessingJobs.AsNoTracking().SingleAsync(j => j.UploadId == upload.UploadId, Ct));

    /// <summary>The <see cref="VideoPublished"/> rows in the outbox for this upload's video, published to Kafka or not.</summary>
    private static async Task<List<VideoPublished>> PublishedEventsAsync(MediaFixture app, SampleUpload upload)
    {
        var key = upload.VideoId.ToString();
        var payloads = await app.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT payload::text AS \"Value\" FROM outbox_messages WHERE topic = {VideoPublished.Topic} AND key = {key}")
            .ToListAsync(Ct));
        return [.. payloads.Select(p => JsonSerializer.Deserialize<VideoPublished>(p, JsonSerializerOptions.Web)!)];
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Not true within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < deadline, $"{path} did not appear within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        }
    }
}
