using Microsoft.Extensions.Options;

namespace NewStore.Media;

/// <summary>
/// Every <see cref="UploadOptions.ExpirySweepInterval"/>: expires the uploads nobody finished and aborts their S3 uploads, which would
/// otherwise keep their stored parts (and, on S3, their charges). The timer runs on the <see cref="TimeProvider"/>, so tests move it with the clock.
/// </summary>
public sealed partial class UploadExpiryService(
    IServiceScopeFactory scopes, TimeProvider time, IOptions<UploadOptions> options, ILogger<UploadExpiryService> logger) : BackgroundService
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
            var expired = await scope.ServiceProvider.GetRequiredService<UploadService>().ExpireAbandonedAsync(ct);
            if (expired > 0)
            {
                Expired(logger, expired);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed pass (database or S3 down) is logged; the next tick tries again and must not be prevented by this one.
            SweepFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} abandoned upload(s).")]
    private static partial void Expired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The upload expiry pass failed; it runs again on the next tick.")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
