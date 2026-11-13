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
    /// The creator who owns the video, or null when Catalog has no such video. A failure to get an answer throws
    /// <see cref="HttpRequestException"/> or <see cref="TransientFailureException"/>.
    /// </summary>
    public async Task<Guid?> GetVideoOwnerAsync(Guid videoId, CancellationToken ct)
    {
        try
        {
            return await GetOwnerCoreAsync(videoId, ct);
        }
        catch (ExecutionRejectedException ex)
        {
            // The pipeline gave up without an answer: its timeout, its open circuit breaker or its concurrency limiter
            // (TimeoutRejectedException, BrokenCircuitException, RateLimiterRejectedException).
            throw new TransientFailureException("Catalog could not be reached through the resilience pipeline.", ex);
        }
    }

    private async Task<Guid?> GetOwnerCoreAsync(Guid videoId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/videos/{videoId}/owner", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var owner = await response.Content.ReadFromJsonAsync<VideoOwner>(ct);
        return owner?.CreatorId;
    }

    // The part of Catalog's reply Inventory reads. Catalog owns the full shape.
    private sealed record VideoOwner(Guid VideoId, Guid CreatorId);
}
