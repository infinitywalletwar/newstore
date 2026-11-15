using Amazon.Runtime;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.Media.Storage;

namespace NewStore.Media.Processing;

/// <summary>
/// Takes one claimed job from the original to Published: download, probe, encode, upload, and the one transaction that publishes.
/// It also decides what a failure means (the rule of AGENTS.md for consumers: retry what may pass, fail what cannot):
/// <list type="bullet">
/// <item>a <see cref="PermanentProcessingException"/> fails the job and the upload at once, with its reason;</item>
/// <item>the job's timeout kills FFmpeg and fails it with <c>timed-out</c>;</item>
/// <item>the worker's stop kills FFmpeg and releases the job for another worker;</item>
/// <item>a lost lease stops the run and writes nothing, because another worker holds the job;</item>
/// <item>anything else (S3, the network, the database) is retried after a pause, until the attempts run out.</item>
/// </list>
/// </summary>
public sealed partial class JobRunner(
    JobQueue queue,
    MediaDbContext db,
    IObjectStore store,
    Ffprobe ffprobe,
    HlsEncoder encoder,
    IServiceScopeFactory scopes,
    IOptions<ProcessingOptions> options,
    TimeProvider time,
    ILogger<JobRunner> logger)
{
    /// <summary>
    /// How long one outcome write may take. Outcome writes don't take the worker's stop token: a stop that lands after the encode
    /// must not throw the finished work away, nor leave a failed job Running with its attempt used. Compose's stop grace period
    /// (30 s) leaves room for it.
    /// </summary>
    private static readonly TimeSpan OutcomeBudget = TimeSpan.FromSeconds(10);

    private ProcessingOptions Options => options.Value;

    /// <param name="ct">The worker's stop token. It stops the work (FFmpeg is killed and the job released), not the outcome writes.</param>
    public async Task RunAsync(ClaimedJob job, string workerId, CancellationToken ct)
    {
        if (job.Attempts > Options.MaxAttempts)
        {
            // The worker of the last attempt died holding the job (a crash, an out-of-memory kill), so its lease ran out and nothing
            // was retried. This claim is one too many.
            await WithinBudgetAsync(t => queue.FailAsync(job.JobId, workerId, "retries-exhausted",
                $"Claim {job.Attempts} of at most {Options.MaxAttempts}: the worker of the last attempt stopped without finishing.", t));
            Failed(logger, job.JobId, "retries-exhausted");
            return;
        }

        // One token for the whole run, cancelled by the worker's stop, by the job's timeout or by a lost lease.
        var directory = Path.Combine(Options.WorkDirectory, job.JobId.ToString());
        using var timeout = new CancellationTokenSource(Options.JobTimeout, time);
        using var leaseLost = new CancellationTokenSource();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, leaseLost.Token);
        try
        {
            PublishedVideo video;
            await using (new LeaseKeeper(scopes, job.JobId, workerId, Options.Lease / 4, time, leaseLost, logger))
            {
                video = await ProcessAsync(job, directory, run.Token);
            }

            await WithinBudgetAsync(t => queue.SucceedAsync(job.JobId, workerId, video, t));
            Published(logger, job.JobId, job.UploadId);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            // Another worker holds the job now, and it writes the outcome.
            LeaseLost(logger, job.JobId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping. Not the job's fault, so its attempt is given back.
            await WithinBudgetAsync(t => queue.ReleaseAsync(job.JobId, workerId, t));
            Released(logger, job.JobId);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await WithinBudgetAsync(t => queue.FailAsync(job.JobId, workerId, "timed-out", $"The job ran longer than {Options.JobTimeout}.", t));
            Failed(logger, job.JobId, "timed-out");
        }
        catch (PermanentProcessingException ex)
        {
            await WithinBudgetAsync(t => queue.FailAsync(job.JobId, workerId, ex.Reason, ex.Message, t));
            Failed(logger, job.JobId, ex.Reason);
        }
        catch (Exception ex)
        {
            var error = Describe(ex);
            if (job.Attempts >= Options.MaxAttempts)
            {
                await WithinBudgetAsync(t => queue.FailAsync(job.JobId, workerId, "retries-exhausted", error, t));
                Failed(logger, job.JobId, "retries-exhausted");
            }
            else
            {
                await WithinBudgetAsync(t => queue.RetryLaterAsync(job.JobId, workerId, error, t));
                WillRetry(logger, ex, job.JobId, job.Attempts);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task WithinBudgetAsync(Func<CancellationToken, Task> write)
    {
        using var budget = new CancellationTokenSource(OutcomeBudget);
        await write(budget.Token);
    }

    private async Task<PublishedVideo> ProcessAsync(ClaimedJob job, string directory, CancellationToken ct)
    {
        var upload = await db.Uploads.AsNoTracking()
            .Where(u => u.Id == job.UploadId)
            .Select(u => new { u.VideoId, u.ObjectKey })
            .SingleAsync(ct);

        // Every path is built from ids, never from the name the creator's file had.
        var source = Path.Combine(directory, "source");
        var output = Path.Combine(directory, "out");
        Directory.CreateDirectory(output);

        await store.DownloadAsync(upload.ObjectKey, source, ct);
        var info = await ffprobe.ProbeAsync(source, ct);
        await encoder.EncodeAsync(source, info, output, ct);
        await encoder.ThumbnailAsync(source, info, output, ct);

        var prefix = ObjectKeys.HlsPrefix(upload.VideoId, job.UploadId);
        await HlsEncoder.UploadOutputAsync(store, output, prefix, ct);
        return new PublishedVideo(
            $"{prefix}/{ObjectKeys.Master}",
            $"{prefix}/{ObjectKeys.Thumbnail}",
            Math.Max(1, (int)Math.Round(info.DurationSeconds, MidpointRounding.AwayFromZero)));
    }

    /// <summary>The job's <c>last_error</c> for a transient failure: S3's status and code say most about it.</summary>
    private static string Describe(Exception ex) => ex is AmazonServiceException service
        ? $"{ex.GetType().Name} {(int)service.StatusCode} {service.ErrorCode}: {ex.Message}"
        : $"{ex.GetType().Name}: {ex.Message}";

    private void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start of the worker empties its whole directory.
            CleanupFailed(logger, ex, directory);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} published upload {UploadId}.")]
    private static partial void Published(ILogger logger, Guid jobId, Guid uploadId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} failed: {Reason}.")]
    private static partial void Failed(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} failed on attempt {Attempt} and will be tried again.")]
    private static partial void WillRetry(ILogger logger, Exception exception, Guid jobId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {JobId} was released: the worker is stopping.")]
    private static partial void Released(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {JobId} lost its lease to another worker; this run stopped and wrote nothing.")]
    private static partial void LeaseLost(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not renew the lease of job {JobId}; trying again on the next tick.")]
    private static partial void RenewFailed(ILogger logger, Exception exception, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Directory}.")]
    private static partial void CleanupFailed(ILogger logger, Exception exception, string directory);

    /// <summary>
    /// Renews the lease every quarter of it while the job runs, each time in a scope of its own (a <see cref="DbContext"/> is not
    /// safe to share with the run). A renewal that finds the job taken cancels <c>leaseLost</c>, which stops the run. A renewal that
    /// fails (the database is briefly away) is tried on the next tick: the lease has three more before it runs out.
    /// </summary>
    private sealed class LeaseKeeper : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewing;

        public LeaseKeeper(
            IServiceScopeFactory scopes, Guid jobId, string workerId, TimeSpan every, TimeProvider time, CancellationTokenSource leaseLost,
            ILogger logger)
        {
            _renewing = RenewAsync(scopes, jobId, workerId, every, time, leaseLost, logger, _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _renewing;
            _stop.Dispose();
        }

        private static async Task RenewAsync(
            IServiceScopeFactory scopes, Guid jobId, string workerId, TimeSpan every, TimeProvider time, CancellationTokenSource leaseLost,
            ILogger logger, CancellationToken stop)
        {
            using var timer = new PeriodicTimer(every, time);
            try
            {
                while (await timer.WaitForNextTickAsync(stop))
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        if (!await scope.ServiceProvider.GetRequiredService<JobQueue>().RenewAsync(jobId, workerId, stop))
                        {
                            await leaseLost.CancelAsync();
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RenewFailed(logger, ex, jobId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The run ended.
            }
        }
    }
}
