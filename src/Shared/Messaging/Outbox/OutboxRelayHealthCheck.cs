using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NewStore.Messaging.Outbox;

/// <summary>
/// Liveness of the relay loop: <c>Unhealthy</c> when the last pass ended more than
/// <c>3 × RelayInterval + ProduceTimeout</c> ago. A pass that fails because Kafka is down still ends, so an
/// outage keeps this check <c>Healthy</c>; only a loop that is stuck or dead fails it. Kafka is deliberately
/// in no readiness check.
/// </summary>
public sealed class OutboxRelayHealthCheck(
    RelayHeartbeat heartbeat, IOptions<MessagingOptions> options, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var limit = (3 * settings.RelayInterval) + settings.ProduceTimeout;

        // Before the first pass there is nothing to measure; the relay stamps as soon as it starts.
        if (heartbeat.LastLoopAt is not { } last)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The outbox relay has not run yet."));
        }

        var age = timeProvider.GetUtcNow() - last;
        return Task.FromResult(age > limit
            ? HealthCheckResult.Unhealthy($"The outbox relay's last loop ended {age.TotalSeconds:F0} s ago (limit {limit.TotalSeconds:F0} s).")
            : HealthCheckResult.Healthy());
    }
}
