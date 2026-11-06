using Microsoft.Extensions.Options;

namespace NewStore.Inventory;

/// <summary>
/// Every <see cref="InventoryOptions.ExpirySweepInterval"/>, expires the Pending reservations that ran out of
/// time and returns their copies. The timer runs on the <see cref="TimeProvider"/>, so tests move it with the clock.
/// </summary>
public sealed partial class ReservationExpiryService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    SweeperHeartbeat heartbeat,
    IOptions<InventoryOptions> options,
    ILogger<ReservationExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ExpirySweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var expired = await scope.ServiceProvider.GetRequiredService<ReservationService>()
                .SweepAsync(time.GetUtcNow(), ct);
            if (expired > 0)
            {
                Expired(logger, expired);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed sweep (database down) is retried on the next tick; it must not end the loop.
            SweepFailed(logger, ex);
        }

        // After every loop, failed or not: the heartbeat says "the loop is turning". A hung sweep never gets here.
        heartbeat.Beat();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} reservation(s) and returned their copies.")]
    private static partial void Expired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The reservation sweep failed; it runs again on the next tick.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
