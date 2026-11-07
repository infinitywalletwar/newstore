using System.Net.Http.Json;

namespace NewStore.Inventory.Tests;

public sealed record EditionDto(
    Guid Id, Guid VideoId, Guid CreatorId, string Format, decimal Price, int CopiesTotal, int CopiesLeft, int CopiesSold,
    DateTimeOffset? LockedAt, DateTimeOffset? EndedAt);

public sealed record ReservationDto(
    Guid Id, Guid EditionId, Guid BuyerId, string Status, DateTimeOffset ReservedAt, DateTimeOffset ExpiresAt, int? CopyNumber);

public sealed record CopyDto(int Number, Guid OwnerId, DateTimeOffset AssignedAt);

public sealed record ProblemDto(string? Type, int Status, string? Title);

/// <summary>The service's endpoints as the tests use them.</summary>
internal static class Api
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<EditionDto> CreateEditionAsync(this HttpClient client, int copies = 10)
    {
        var response = await client.PostAsJsonAsync("/editions", new
        {
            videoId = Guid.NewGuid(),
            creatorId = Guid.NewGuid(),
            format = "Digital",
            price = 9.99m,
            copies,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EditionDto>(Ct))!;
    }

    public static async Task<EditionDto> GetEditionAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<EditionDto>($"/editions/{id}", Ct))!;

    public static Task<HttpResponseMessage> UpdateEditionAsync(
        this HttpClient client, Guid id, decimal? price = null, int? copies = null) =>
        client.PatchAsJsonAsync($"/editions/{id}", new { price, copies }, Ct);

    public static Task<HttpResponseMessage> EndSaleAsync(this HttpClient client, Guid id) =>
        client.PostAsync($"/editions/{id}/end-sale", content: null, Ct);

    public static Task<HttpResponseMessage> ReserveAsync(this HttpClient client, Guid editionId, Guid buyerId) =>
        client.PostAsJsonAsync($"/editions/{editionId}/reservations", new { buyerId }, Ct);

    public static async Task<ReservationDto> ReserveOkAsync(this HttpClient client, Guid editionId, Guid? buyerId = null)
    {
        var response = await client.ReserveAsync(editionId, buyerId ?? Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!;
    }

    public static async Task<ReservationDto> GetReservationAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ReservationDto>($"/reservations/{id}", Ct))!;

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, Guid reservationId) =>
        client.PostAsync($"/reservations/{reservationId}/confirm", content: null, Ct);

    public static Task<HttpResponseMessage> ReleaseAsync(this HttpClient client, Guid reservationId) =>
        client.PostAsync($"/reservations/{reservationId}/release", content: null, Ct);

    public static async Task<CopyDto[]> GetCopiesAsync(this HttpClient client, Guid editionId) =>
        (await client.GetFromJsonAsync<CopyDto[]>($"/editions/{editionId}/copies", Ct))!;

    public static async Task<ProblemDto> ReadProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Ct))!;
}
