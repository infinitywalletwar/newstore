using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NewStore.Inventory;

/// <summary>When the expiry sweeper last finished a loop. The sweeper stamps it; the live check reads it.</summary>
public sealed class SweeperHeartbeat(TimeProvider time)
{
    // The service just started: give the sweeper one limit to make its first loop.
    private long _lastBeatTicks = time.GetUtcNow().UtcTicks;

    public DateTimeOffset LastBeat => new(Interlocked.Read(ref _lastBeatTicks), TimeSpan.Zero);

    public void Beat() => Interlocked.Exchange(ref _lastBeatTicks, time.GetUtcNow().UtcTicks);
}

/// <summary>
/// Liveness check "reservation-sweeper": Unhealthy when the last loop is older than 3 sweep intervals + 5 s.
/// A sweeper that hangs or died stops reservations from expiring, so restarting the process is the cure.
/// </summary>
public sealed class SweeperHealthCheck(
    SweeperHeartbeat heartbeat, TimeProvider time, IOptions<InventoryOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var limit = 3 * options.Value.ExpirySweepInterval + TimeSpan.FromSeconds(5);
        var age = time.GetUtcNow() - heartbeat.LastBeat;
        return Task.FromResult(age > limit
            ? HealthCheckResult.Unhealthy($"The sweeper's last loop was {age.TotalSeconds:F0} s ago; the limit is {limit.TotalSeconds:F0} s.")
            : HealthCheckResult.Healthy());
    }
}
