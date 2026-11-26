using Microsoft.Extensions.Options;

namespace NewStore.Delivery.Courier;

/// <summary>
/// The courier container's loop: a <see cref="PeriodicTimer"/> on <see cref="CourierOptions.TickInterval"/> over
/// <see cref="TimeProvider"/> (so tests tick it by moving a fake clock) that calls <see cref="CourierRunner.TickAsync"/>. On a
/// graceful stop it releases the courier's leases, so another courier carries the trips on at its next tick instead of after the
/// lease runs out. A courier that is killed releases nothing; its leases run out instead.
/// </summary>
public sealed partial class CourierService(
    CourierRunner runner, TimeProvider time, IOptions<CourierOptions> options, ILogger<CourierService> logger) : BackgroundService
{
    /// <summary>How long the release on stop may take. Wall-clock time: a fake clock in a test must not hold up a shutdown.</summary>
    private static readonly TimeSpan ReleaseBudget = TimeSpan.FromSeconds(5);

    private PeriodicTimer? _timer;

    /// <summary>
    /// Creates the timer before the host finishes starting. Since .NET 10 all of <c>ExecuteAsync</c> runs on a background thread
    /// (https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task), so a timer made
    /// there could start after a test has already moved the clock, and that tick would be lost.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(options.Value.TickInterval, time);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Started(logger, runner.CourierId);
        try
        {
            while (await _timer!.WaitForNextTickAsync(stoppingToken))
            {
                await runner.TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Stops the loop (the base waits for the tick in progress), then releases the leases within the budget.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ReleaseBudget);
        try
        {
            await runner.ReleaseAsync(budget.Token);
        }
        catch (Exception ex)
        {
            // The database is away or slow: the leases run out on their own, so another courier still takes the trips over.
            ReleaseFailed(logger, runner.CourierId, ex);
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Courier {CourierId} started.")]
    private static partial void Started(ILogger logger, string courierId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Courier {CourierId} could not release its leases; they run out on their own.")]
    private static partial void ReleaseFailed(ILogger logger, string courierId, Exception exception);
}
