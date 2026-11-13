using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NewStore.Inventory.Tests;

public sealed record EditionDto(
    Guid Id, Guid VideoId, Guid CreatorId, string Format, decimal Price, int CopiesTotal, int CopiesLeft, int CopiesSold,
    DateTimeOffset? LockedAt, DateTimeOffset? EndedAt);

public sealed record ReservationDto(
    Guid Id, Guid EditionId, Guid BuyerId, Guid VideoId, string Format, decimal Price, string Status,
    DateTimeOffset ReservedAt, DateTimeOffset ExpiresAt, int? CopyNumber);

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

    /// <summary>The token of the one caller the reservation endpoints accept: the <c>orders</c> client, for audience <c>inventory</c>.</summary>
    private static HttpRequestMessage AsOrders(HttpMethod method, string url, object? body = null) =>
        new(method, url)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Client("orders", "inventory")) },
            Content = body is null ? null : JsonContent.Create(body),
        };

    private static Task<HttpResponseMessage> SendAsOrders(this HttpClient client, HttpMethod method, string url, object? body = null) =>
        client.SendAsync(AsOrders(method, url, body), Ct);

    /// <summary>Reserves with a new reservation id unless the test passes one.</summary>
    public static Task<HttpResponseMessage> ReserveAsync(
        this HttpClient client, Guid editionId, Guid buyerId, Guid? reservationId = null) =>
        client.SendAsOrders(
            HttpMethod.Post, $"/editions/{editionId}/reservations",
            new { reservationId = reservationId ?? Guid.NewGuid(), buyerId });

    public static async Task<ReservationDto> ReserveOkAsync(this HttpClient client, Guid editionId, Guid? buyerId = null)
    {
        var response = await client.ReserveAsync(editionId, buyerId ?? Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!;
    }

    public static async Task<ReservationDto> GetReservationAsync(this HttpClient client, Guid id)
    {
        var response = await client.SendAsOrders(HttpMethod.Get, $"/reservations/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!;
    }

    public static Task<HttpResponseMessage> HoldAsync(this HttpClient client, Guid reservationId) =>
        client.SendAsOrders(HttpMethod.Post, $"/reservations/{reservationId}/hold");

    public static async Task<ReservationDto> HoldOkAsync(this HttpClient client, Guid reservationId)
    {
        var response = await client.HoldAsync(reservationId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!;
    }

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, Guid reservationId) =>
        client.SendAsOrders(HttpMethod.Post, $"/reservations/{reservationId}/confirm");

    public static Task<HttpResponseMessage> ReleaseAsync(this HttpClient client, Guid reservationId) =>
        client.SendAsOrders(HttpMethod.Post, $"/reservations/{reservationId}/release");

    public static async Task<CopyDto[]> GetCopiesAsync(this HttpClient client, Guid editionId) =>
        (await client.GetFromJsonAsync<CopyDto[]>($"/editions/{editionId}/copies", Ct))!;

    public static async Task<ProblemDto> ReadProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Ct))!;
}
