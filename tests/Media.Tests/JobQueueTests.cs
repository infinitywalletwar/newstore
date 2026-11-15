using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Media.Processing;

using static NewStore.Media.Tests.Api;

namespace NewStore.Media.Tests;

/// <summary>
/// The queue's statements against real PostgreSQL, with the fixture's fake clock: the role is Api, so no worker claims anything
/// but the test. Each test starts from an empty table.
/// </summary>
public sealed class JobQueueTests(MediaFixture media) : IClassFixture<MediaFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTimeOffset Now => Micro(media.Clock.GetUtcNow());

    public async ValueTask InitializeAsync() =>
        // The class shares one database, and a job an earlier test left Running is due again once the clock passes its lease.
        await media.WithDbAsync(async db => await db.ProcessingJobs.ExecuteDeleteAsync(Ct) + await db.Uploads.ExecuteDeleteAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Claim_takes_the_oldest_due_job_and_counts_the_attempt()
    {
        var newer = await AddJobAsync(Now.AddSeconds(-1));
        var oldest = await AddJobAsync(Now.AddSeconds(-10));
        await AddJobAsync(Now.AddMinutes(1));   // not due yet

        var claim = await ClaimAsync("worker-a");

        Assert.NotNull(claim);
        Assert.Equal(oldest, claim.JobId);
        Assert.Equal(await UploadOfAsync(oldest), claim.UploadId);
        Assert.Equal(1, claim.Attempts);
        var job = await JobAsync(oldest);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal("worker-a", job.LockedBy);
        Assert.Equal(Now + Lease, job.LockedUntil);
        Assert.Equal(Now, job.UpdatedAt);

        Assert.Equal(newer, (await ClaimAsync("worker-a"))?.JobId);
        Assert.Null(await ClaimAsync("worker-a"));   // the third is not due
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_job()
    {
        for (var i = 0; i < 20; i++)
        {
            await AddJobAsync(Now.AddMilliseconds(-i));
        }

        // Four claimers, each with its own scope and connection, claim until nothing is left.
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            var mine = new List<ClaimedJob>();
            while (await ClaimAsync($"worker-{worker}") is { } claim)
            {
                mine.Add(claim);
            }

            return mine;
        }, Ct)));

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Select(c => c.JobId).Distinct().Count());
        Assert.All(all, claim => Assert.Equal(1, claim.Attempts));
    }

    [Fact]
    public async Task Expired_lease_is_taken_over_by_another_worker()
    {
        var id = await AddJobAsync(Now);
        Assert.Equal(id, (await ClaimAsync("a"))?.JobId);

        // While the lease runs, the job is a's.
        media.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await ClaimAsync("b"));

        // a stopped renewing (it died): a second past the lease, b takes the same job, and the claim counts a second attempt.
        media.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var takeover = await ClaimAsync("b");

        Assert.NotNull(takeover);
        Assert.Equal(id, takeover.JobId);
        Assert.Equal(2, takeover.Attempts);
        Assert.False(await WithQueueAsync(q => q.RenewAsync(id, "a", Ct)));

        // Whatever a still writes changes nothing: the job stays b's.
        await WithQueueAsync(async q =>
        {
            await q.ReleaseAsync(id, "a", Ct);
            await q.RetryLaterAsync(id, "a", "late", Ct);
            await q.FailAsync(id, "a", "encode-failed", "late", Ct);
            return 0;
        });
        var job = await JobAsync(id);
        Assert.Equal((JobStatus.Running, "b", 2), (job.Status, job.LockedBy, job.Attempts));
        Assert.True(await WithQueueAsync(q => q.RenewAsync(id, "b", Ct)));
    }

    [Fact]
    public async Task Retry_later_waits_for_the_backoff()
    {
        var id = await AddJobAsync(Now);
        await ClaimAsync("a");

        await WithQueueAsync(async q => { await q.RetryLaterAsync(id, "a", "S3 answered 503", Ct); return 0; });

        var job = await JobAsync(id);
        Assert.Equal((JobStatus.Pending, null, null), (job.Status, job.LockedBy, job.LockedUntil));
        Assert.Equal("S3 answered 503", job.LastError);
        Assert.Equal(Now + TimeSpan.FromSeconds(30), job.NextRunAt);   // RetryPause × 2^0
        media.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await ClaimAsync("a"));
        media.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await ClaimAsync("a");
        Assert.Equal((id, 2), (second?.JobId, second?.Attempts));

        // The pause doubles: after the second attempt it is a minute.
        await WithQueueAsync(async q => { await q.RetryLaterAsync(id, "a", "S3 answered 503 again", Ct); return 0; });
        Assert.Equal(Now + TimeSpan.FromMinutes(1), (await JobAsync(id)).NextRunAt);
    }

    [Fact]
    public async Task Release_puts_the_job_back_without_using_an_attempt()
    {
        var id = await AddJobAsync(Now);
        await ClaimAsync("a");

        await WithQueueAsync(async q => { await q.ReleaseAsync(id, "a", Ct); return 0; });

        var job = await JobAsync(id);
        Assert.Equal((JobStatus.Pending, 0, null, null), (job.Status, job.Attempts, job.LockedBy, job.LockedUntil));
        Assert.Equal(Now, job.NextRunAt);   // due at once, for any worker
        Assert.Equal((id, 1), ((await ClaimAsync("b"))?.JobId, (await JobAsync(id)).Attempts));
    }

    private Task<ClaimedJob?> ClaimAsync(string workerId) => WithQueueAsync(q => q.ClaimAsync(workerId, Ct));

    private async Task<T> WithQueueAsync<T>(Func<JobQueue, Task<T>> action)
    {
        await using var scope = media.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JobQueue>());
    }

    private Task<ProcessingJob> JobAsync(Guid id) =>
        media.WithDbAsync(db => db.ProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct));

    private Task<Guid> UploadOfAsync(Guid jobId) =>
        media.WithDbAsync(db => db.ProcessingJobs.Where(j => j.Id == jobId).Select(j => j.UploadId).SingleAsync(Ct));

    /// <summary>A Processing upload and its Pending job, due at <paramref name="nextRunAt"/>, as complete would write them.</summary>
    private async Task<Guid> AddJobAsync(DateTimeOffset nextRunAt)
    {
        var now = Now;
        var upload = new Upload
        {
            Id = Guid.CreateVersion7(),
            VideoId = Guid.NewGuid(),
            CreatorId = Guid.NewGuid(),
            FileName = "trip.mp4",
            ContentType = "video/mp4",
            SizeBytes = MiB,
            ObjectKey = "originals/test",
            S3UploadId = "test",
            PartSize = MiB,
            PartCount = 1,
            Status = UploadStatus.Processing,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
        };
        var job = new ProcessingJob
        {
            Id = Guid.CreateVersion7(),
            UploadId = upload.Id,
            Status = JobStatus.Pending,
            NextRunAt = Micro(nextRunAt),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await media.WithDbAsync(db =>
        {
            db.Uploads.Add(upload);
            db.ProcessingJobs.Add(job);
            return db.SaveChangesAsync(Ct);
        });
        return job.Id;
    }
}
