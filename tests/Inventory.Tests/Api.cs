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

    /// <summary>A signed-in user's token for this service: client <c>gateway</c>, audience <c>inventory</c>.</summary>
    public static string UserToken(Guid userId) => TestTokens.User(userId, "inventory");

    /// <summary>
    /// Creates an edition as <paramref name="creatorId"/> (a new creator unless given) on <paramref name="videoId"/> (a new video
    /// that creator owns, unless given). The fake Catalog is told who owns the video, as the real one would know.
    /// </summary>
    public static async Task<EditionDto> CreateEditionAsync(
        this HttpClient client, int copies = 10, Guid? creatorId = null, Guid? videoId = null)
    {
        var creator = creatorId ?? Guid.NewGuid();
        var video = videoId ?? Guid.NewGuid();
        FakeCatalog.SetOwner(video, creator);
        var response = await client.SendAsUserAsync(creator, HttpMethod.Post, "/editions", new
        {
            videoId = video,
            format = "Digital",
            price = 9.99m,
            copies,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EditionDto>(Ct))!;
    }

    public static async Task<EditionDto> GetEditionAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<EditionDto>($"/editions/{id}", Ct))!;

    /// <summary>Sends the request with the token of <paramref name="userId"/>.</summary>
    public static Task<HttpResponseMessage> SendAsUserAsync(
        this HttpClient client, Guid userId, HttpMethod method, string url, object? body = null) =>
        client.SendAsync(new HttpRequestMessage(method, url)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", UserToken(userId)) },
            Content = body is null ? null : JsonContent.Create(body),
        }, Ct);

    /// <summary>
    /// Sends the request as <paramref name="userId"/>, or as the edition's creator (read from the public GET) when the test
    /// gives none. An unknown edition has no creator, so the token is a stranger's and the answer is the 404.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsCreatorAsync(
        this HttpClient client, Guid editionId, Guid? userId, HttpMethod method, string url, object? body = null)
    {
        var user = userId ?? (await client.GetAsync($"/editions/{editionId}", Ct) is { IsSuccessStatusCode: true } found
            ? (await found.Content.ReadFromJsonAsync<EditionDto>(Ct))!.CreatorId
            : Guid.NewGuid());
        return await client.SendAsUserAsync(user, method, url, body);
    }

    public static Task<HttpResponseMessage> UpdateEditionAsync(
        this HttpClient client, Guid id, decimal? price = null, int? copies = null, Guid? asUser = null) =>
        client.SendAsCreatorAsync(id, asUser, HttpMethod.Patch, $"/editions/{id}", new { price, copies });

    public static Task<HttpResponseMessage> EndSaleAsync(this HttpClient client, Guid id, Guid? asUser = null) =>
        client.SendAsCreatorAsync(id, asUser, HttpMethod.Post, $"/editions/{id}/end-sale");

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

    public static Task<HttpResponseMessage> GetCopiesResponseAsync(this HttpClient client, Guid editionId, Guid? asUser = null) =>
        client.SendAsCreatorAsync(editionId, asUser, HttpMethod.Get, $"/editions/{editionId}/copies");

    public static async Task<CopyDto[]> GetCopiesAsync(this HttpClient client, Guid editionId)
    {
        var response = await client.GetCopiesResponseAsync(editionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CopyDto[]>(Ct))!;
    }

    public static async Task<ProblemDto> ReadProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Ct))!;
}
