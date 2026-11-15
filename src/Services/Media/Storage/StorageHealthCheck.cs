using Amazon.S3.Model;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NewStore.Media;

/// <summary>
/// Readiness check: can Media reach its bucket? The registration gives it a 3 s budget and the status to report on failure,
/// Degraded: Media still answers status reads without S3, as Catalog does without Redis (ADR 0009).
/// </summary>
internal sealed class StorageHealthCheck(MediaStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Our own budget is a little under the registration's 3 s, so a slow S3 is reported as Degraded by this check
        // and not as the timeout the health check service reports.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2.5));
        try
        {
            await storage.Client.HeadBucketAsync(new HeadBucketRequest { BucketName = storage.Bucket }, budget.Token);
            return HealthCheckResult.Healthy("The bucket answers.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "S3 does not answer for the bucket.", ex);
        }
    }
}
