using System.Net;
using System.Net.Http.Json;

using NewStore.Messaging;

using Polly;

namespace NewStore.Inventory;

/// <summary>
/// Inventory's view of Catalog. Registered with <c>AddServiceClient</c>, so every call carries Inventory's own token and goes
/// through the standard resilience pipeline. When that pipeline gives up without an answer it throws Polly's own exceptions;
/// this class turns them into <see cref="TransientFailureException"/>, as Payments' FakePay client does.
/// </summary>
public sealed class CatalogClient(HttpClient http)
{
    /// <summary>
    /// The video's creator and status, or null when Catalog has no such video. A failure to get an answer throws
    /// <see cref="HttpRequestException"/> or <see cref="TransientFailureException"/>.
    /// </summary>
    public async Task<CatalogVideo?> GetVideoAsync(Guid videoId, CancellationToken ct)
    {
        try
        {
            return await GetVideoCoreAsync(videoId, ct);
        }
        catch (ExecutionRejectedException ex)
        {
            // The pipeline gave up without an answer: its timeout, its open circuit breaker or its concurrency limiter
            // (TimeoutRejectedException, BrokenCircuitException, RateLimiterRejectedException).
            throw new TransientFailureException("Catalog could not be reached through the resilience pipeline.", ex);
        }
    }

    private async Task<CatalogVideo?> GetVideoCoreAsync(Guid videoId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/videos/{videoId}/owner", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogVideo>(ct);
    }
}

/// <summary>The part of Catalog's <c>/videos/{id}/owner</c> reply Inventory reads. Catalog owns the full shape.</summary>
public sealed record CatalogVideo(Guid VideoId, Guid CreatorId, string Status)
{
    public bool IsPublished => Status == "Published";
}
