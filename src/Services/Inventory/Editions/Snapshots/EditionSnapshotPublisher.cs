using Microsoft.Extensions.Options;

using NewStore.Messaging;

namespace NewStore.Inventory;

/// <summary>
/// Every <see cref="InventoryOptions.SnapshotInterval"/>, runs <see cref="EditionSnapshots.PublishAsync"/>. While a
/// pass fills its batch, more editions are probably waiting, so the next pass starts without the wait. The timer
/// runs on the <see cref="TimeProvider"/>, so tests move it with the clock.
/// </summary>
public sealed partial class EditionSnapshotPublisher(
    IServiceScopeFactory scopes,
    TimeProvider time,
    EditionSnapshotHeartbeat heartbeat,
    IOptions<InventoryOptions> options,
    ILogger<EditionSnapshotPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SnapshotInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var batchSize = options.Value.SnapshotBatchSize;
        try
        {
            int? sent;
            do
            {
                await using var scope = scopes.CreateAsyncScope();
                sent = await EditionSnapshots.PublishAsync(
                    scope.ServiceProvider.GetRequiredService<InventoryDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IOutbox>(),
                    time.GetUtcNowToMicrosecond(),
                    batchSize,
                    ct);
                if (sent > 0)
                {
                    Published(logger, sent.Value);
                }
            }
            while (sent == batchSize);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed pass (database down) is retried on the next tick; it must not end the loop.
            PassFailed(logger, ex);
        }

        // After every tick, failed or not: the heartbeat says "the loop is turning". A hung pass never gets here.
        heartbeat.Beat();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enqueued {Count} edition snapshot(s).")]
    private static partial void Published(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The edition snapshot pass failed; it runs again on the next tick.")]
    private static partial void PassFailed(ILogger logger, Exception exception);
}
