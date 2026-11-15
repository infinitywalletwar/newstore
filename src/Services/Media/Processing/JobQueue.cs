using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.Contracts.Media;
using NewStore.Messaging;

namespace NewStore.Media.Processing;

/// <summary>A job this worker now holds: <see cref="Attempts"/> counts this claim.</summary>
public sealed record ClaimedJob(Guid JobId, Guid UploadId, int Attempts);

/// <summary>What a finished job made, relative to the public media base URL.</summary>
public sealed record PublishedVideo(string PlaylistPath, string ThumbnailPath, int DurationSeconds);

/// <summary>
/// The work queue in <c>processing_jobs</c>. A claim takes a job with a lease (<c>locked_by</c>, <c>locked_until</c>); every later
/// change is guarded by <c>locked_by = worker AND status = 'Running'</c>, so a worker whose lease another worker has taken changes
/// nothing. Every "now" comes from <see cref="TimeProvider"/> as a parameter, never <c>now()</c>, so tests move time.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name is the design's: a work queue kept in a table, not a System.Collections.Queue.")]
public sealed class JobQueue(MediaDbContext db, IOutbox outbox, TimeProvider time, IOptions<ProcessingOptions> options)
{
    private ProcessingOptions Options => options.Value;

    /// <summary>
    /// Takes the job that has waited longest: a Pending one that is due, or a Running one whose lease ran out because its worker
    /// stopped renewing. Null when there is none. The claim counts an attempt, so a job that kills its worker every time runs out.
    /// </summary>
    public async Task<ClaimedJob?> ClaimAsync(string workerId, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        var lockedUntil = now + Options.Lease;

        // One statement. The subquery's FOR UPDATE locks the row it picks until the statement commits, and SKIP LOCKED makes a
        // second worker's subquery pass over that row instead of waiting for it (PostgreSQL, SELECT, "The Locking Clause"), so two
        // claims never return one job. The data-modifying statement stays at the top level, so it is not composed with LINQ:
        // AsNoTracking and ToListAsync add no SQL, and the single row is taken here.
        var claimed = await db.ProcessingJobs
            .FromSql($"""
                UPDATE processing_jobs SET status = 'Running', attempts = attempts + 1, locked_by = {workerId},
                       locked_until = {lockedUntil}, updated_at = {now}
                WHERE id = (SELECT id FROM processing_jobs
                            WHERE (status = 'Pending' AND next_run_at <= {now}) OR (status = 'Running' AND locked_until < {now})
                            ORDER BY next_run_at LIMIT 1 FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(ct);

        return claimed.SingleOrDefault() is { } job ? new ClaimedJob(job.Id, job.UploadId, job.Attempts) : null;
    }

    /// <summary>Moves the lease on. False when the job is no longer this worker's: its lease ran out and another worker took it.</summary>
    public async Task<bool> RenewAsync(Guid jobId, string workerId, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        var renewed = await Held(jobId, workerId).ExecuteUpdateAsync(set => set
            .SetProperty(j => j.LockedUntil, now + Options.Lease)
            .SetProperty(j => j.UpdatedAt, now), ct);
        return renewed == 1;
    }

    /// <summary>
    /// A failure that may pass: the job waits <c>RetryPause × 2^(attempts − 1)</c>, at most <c>MaxRetryPause</c>, and is Pending
    /// again for any worker.
    /// </summary>
    public async Task RetryLaterAsync(Guid jobId, string workerId, string error, CancellationToken ct)
    {
        if (await Held(jobId, workerId).Select(j => (int?)j.Attempts).SingleOrDefaultAsync(ct) is not { } attempts)
        {
            return;
        }

        var now = time.GetUtcNowToMicrosecond();
        await Held(jobId, workerId).ExecuteUpdateAsync(set => set
            .SetProperty(j => j.Status, JobStatus.Pending)
            .SetProperty(j => j.NextRunAt, now + Backoff(attempts))
            .SetProperty(j => j.LastError, error)
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.UpdatedAt, now), ct);
    }

    /// <summary>The job fails for good, and in the same transaction its upload is Failed with <paramref name="reason"/>.</summary>
    public async Task FailAsync(Guid jobId, string workerId, string reason, string error, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await FinishAsync(jobId, workerId, JobStatus.Failed, error, now, ct) is not { } uploadId)
        {
            return;
        }

        await db.Uploads
            .Where(u => u.Id == uploadId && u.Status == UploadStatus.Processing)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.Status, UploadStatus.Failed)
                .SetProperty(u => u.FailureReason, reason)
                .SetProperty(u => u.UpdatedAt, now), ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The worker is stopping: the job is Pending and due at once for another worker, and the claim's attempt is given back, because
    /// the job did not fail.
    /// </summary>
    public async Task ReleaseAsync(Guid jobId, string workerId, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        await Held(jobId, workerId).ExecuteUpdateAsync(set => set
            .SetProperty(j => j.Status, JobStatus.Pending)
            .SetProperty(j => j.Attempts, j => j.Attempts - 1)
            .SetProperty(j => j.NextRunAt, now)
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.UpdatedAt, now), ct);
    }

    /// <summary>
    /// In one transaction: the job Succeeded, the upload Published with its paths, and <see cref="VideoPublished"/> in the outbox.
    /// A worker that lost the lease writes nothing; the one that holds it publishes.
    /// </summary>
    public async Task SucceedAsync(Guid jobId, string workerId, PublishedVideo video, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await FinishAsync(jobId, workerId, JobStatus.Succeeded, error: null, now, ct) is not { } uploadId)
        {
            return;
        }

        var published = await db.Uploads
            .Where(u => u.Id == uploadId && u.Status == UploadStatus.Processing)
            .ExecuteUpdateAsync(set => set
                .SetProperty(u => u.Status, UploadStatus.Published)
                .SetProperty(u => u.PlaylistPath, video.PlaylistPath)
                .SetProperty(u => u.ThumbnailPath, video.ThumbnailPath)
                .SetProperty(u => u.DurationSeconds, video.DurationSeconds)
                .SetProperty(u => u.PublishedAt, now)
                .SetProperty(u => u.UpdatedAt, now), ct);
        if (published == 1)
        {
            var videoId = await db.Uploads.Where(u => u.Id == uploadId).Select(u => u.VideoId).SingleAsync(ct);
            outbox.Enqueue(
                new VideoPublished
                {
                    EventId = Guid.CreateVersion7(),
                    OccurredAt = now,
                    VideoId = videoId,
                    UploadId = uploadId,
                    PlaylistPath = video.PlaylistPath,
                    ThumbnailPath = video.ThumbnailPath,
                    DurationSeconds = video.DurationSeconds,
                },
                VideoPublished.Topic,
                videoId.ToString());
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>The pause before attempt <c>attempts + 1</c>: doubling from <c>RetryPause</c>, capped at <c>MaxRetryPause</c>.</summary>
    internal TimeSpan Backoff(int attempts)
    {
        var doublings = Math.Clamp(attempts - 1, 0, 30);
        var ticks = Math.Min((double)Options.RetryPause.Ticks * Math.Pow(2, doublings), Options.MaxRetryPause.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>The job, if this worker still holds it.</summary>
    private IQueryable<ProcessingJob> Held(Guid jobId, string workerId) =>
        db.ProcessingJobs.Where(j => j.Id == jobId && j.LockedBy == workerId && j.Status == JobStatus.Running);

    /// <summary>
    /// Ends the held job with <paramref name="status"/> and returns its upload's id, or null when the job is not ours. A null
    /// <paramref name="error"/> keeps the last one, so a job that succeeded after a retry still shows what it retried. <c>locked_by</c>
    /// stays as the worker that finished it.
    /// </summary>
    private async Task<Guid?> FinishAsync(Guid jobId, string workerId, JobStatus status, string? error, DateTimeOffset now, CancellationToken ct)
    {
        var finished = await Held(jobId, workerId).ExecuteUpdateAsync(set => set
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.LastError, j => error ?? j.LastError)
            .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.UpdatedAt, now), ct);
        return finished == 1
            ? await db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.UploadId).SingleAsync(ct)
            : null;
    }
}
