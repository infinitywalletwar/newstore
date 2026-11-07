using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NewStore.Inventory;

/// <summary>When the snapshot publisher last finished a tick. The publisher stamps it; the live check reads it.</summary>
public sealed class EditionSnapshotHeartbeat(TimeProvider time)
{
    // The service just started: give the publisher one limit to make its first tick.
    private long _lastBeatTicks = time.GetUtcNow().UtcTicks;

    public DateTimeOffset LastBeat => new(Interlocked.Read(ref _lastBeatTicks), TimeSpan.Zero);

    public void Beat() => Interlocked.Exchange(ref _lastBeatTicks, time.GetUtcNow().UtcTicks);
}

/// <summary>
/// Liveness check "edition-snapshots": Unhealthy when the last tick is older than 3 snapshot intervals + 5 s.
/// A publisher that hangs or died leaves Catalog's copy stale, so restarting the process is the cure.
/// </summary>
public sealed class EditionSnapshotHealthCheck(
    EditionSnapshotHeartbeat heartbeat, TimeProvider time, IOptions<InventoryOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var limit = 3 * options.Value.SnapshotInterval + TimeSpan.FromSeconds(5);
        var age = time.GetUtcNow() - heartbeat.LastBeat;
        return Task.FromResult(age > limit
            ? HealthCheckResult.Unhealthy($"The snapshot publisher's last tick was {age.TotalSeconds:F0} s ago; the limit is {limit.TotalSeconds:F0} s.")
            : HealthCheckResult.Healthy());
    }
}
