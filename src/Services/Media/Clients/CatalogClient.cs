using System.Net;
using System.Net.Http.Json;

using NewStore.Messaging;

using Polly;

namespace NewStore.Media;

/// <summary>What Catalog says about a video. <c>Status</c> is <c>Draft</c> or <c>Published</c>.</summary>
public sealed record CatalogVideo(Guid VideoId, Guid CreatorId, string Status);

/// <summary>
/// Media's view of Catalog, registered with <c>AddServiceClient</c> as client <c>media</c>: every call carries Media's own token and
/// goes through the standard resilience pipeline. As in Inventory's client, Polly's rejections become
/// <see cref="TransientFailureException"/>.
/// </summary>
public sealed class CatalogClient(HttpClient http)
{
    /// <summary>The video, or null when Catalog has none. A failure to get an answer throws <see cref="HttpRequestException"/> or <see cref="TransientFailureException"/>.</summary>
    public async Task<CatalogVideo?> GetVideoAsync(Guid videoId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"/videos/{videoId}/owner", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<CatalogVideo>(ct);
        }
        catch (ExecutionRejectedException ex)
        {
            // The pipeline gave up without an answer: its timeout, its open circuit breaker or its concurrency limiter.
            throw new TransientFailureException("Catalog could not be reached through the resilience pipeline.", ex);
        }
    }
}
