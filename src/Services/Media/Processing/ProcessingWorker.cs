using Microsoft.Extensions.Options;

namespace NewStore.Media.Processing;

/// <summary>
/// The worker's loop: claim a due job, run it, and ask again at once; when none is due, wait <see cref="ProcessingOptions.PollInterval"/>.
/// A hosted service is a singleton, so each job gets a scope of its own for its <see cref="MediaDbContext"/>. The host cancels the
/// stop token on shutdown (SIGTERM in a container); the running job then kills FFmpeg and releases itself before this returns.
/// </summary>
public sealed partial class ProcessingWorker(
    IServiceScopeFactory scopes, IOptions<ProcessingOptions> options, TimeProvider time, ILogger<ProcessingWorker> logger) : BackgroundService
{
    /// <summary>This process in <c>locked_by</c>: the host name (in a container, its id) and a random part, so two processes differ.</summary>
    public string WorkerId { get; } = $"{Environment.MachineName[..Math.Min(Environment.MachineName.Length, 60)]}/{Guid.NewGuid():N}";

    /// <summary>
    /// Empties <c>{WorkDirectory}/jobs</c> before the host finishes starting. A job that was running when this container last died
    /// left its files there; its lease runs out and another claim redoes it. This is not in <see cref="ExecuteAsync"/>: since
    /// .NET 10 all of <c>ExecuteAsync</c> runs on a background thread, so the host could report started before the folder is
    /// empty, while the synchronous part of <c>StartAsync</c> still runs during startup
    /// (https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        EmptyJobsDirectory();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval, time);
        try
        {
            do
            {
                while (await RunNextAsync(stoppingToken))
                {
                    // Every due job before the next wait.
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>True when it ran a job, so the loop asks again without waiting.</summary>
    private async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<JobQueue>().ClaimAsync(WorkerId, stoppingToken) is not { } job)
            {
                return false;
            }

            Claimed(logger, job.JobId, job.UploadId, job.Attempts);
            await scope.ServiceProvider.GetRequiredService<JobRunner>().RunAsync(job, WorkerId, stoppingToken);
            return !stoppingToken.IsCancellationRequested;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // The database is away, or a job's outcome could not be written. The loop goes on: a job left Running keeps its lease
            // until the lease runs out, and then a claim takes it again.
            LoopFailed(logger, ex);
            return false;
        }
    }

    /// <summary>Empties only <c>{WorkDirectory}/jobs</c>, the folder the worker creates: it never deletes a file it did not make.</summary>
    private void EmptyJobsDirectory()
    {
        var directory = Directory.CreateDirectory(Path.Combine(options.Value.WorkDirectory, ProcessingOptions.JobsFolder));
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo folder)
            {
                folder.Delete(recursive: true);
            }
            else
            {
                entry.Delete();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Claimed job {JobId} for upload {UploadId}, attempt {Attempt}.")]
    private static partial void Claimed(ILogger logger, Guid jobId, Guid uploadId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "The processing loop failed; it goes on after the next poll.")]
    private static partial void LoopFailed(ILogger logger, Exception exception);
}
