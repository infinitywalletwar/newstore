using System.Net;
using System.Net.Http.Json;

namespace NewStore.Inventory;

/// <summary>Inventory's view of Catalog. Registered with <c>AddServiceClient</c>, so every call carries Inventory's own token.</summary>
public sealed class CatalogClient(HttpClient http)
{
    /// <summary>The creator who owns the video, or null when Catalog has no such video. Any other failure throws.</summary>
    public async Task<Guid?> GetVideoOwnerAsync(Guid videoId, CancellationToken ct)
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
