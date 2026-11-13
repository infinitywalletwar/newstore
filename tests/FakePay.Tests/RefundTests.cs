using System.Net.Http.Json;

namespace NewStore.FakePay.Tests;

public class RefundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record RefundDto(string Id, string SessionId, decimal Amount, string Status);

    private static async Task<HttpResponseMessage> RefundAsync(HttpClient client, string sessionId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/refunds") { Content = JsonContent.Create(new { sessionId }) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Refund_is_idempotent()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);
        await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);

        var first = await RefundAsync(client, session.Id, "refund-1");
        var again = await RefundAsync(client, session.Id, "refund-1");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstRefund = (await first.Content.ReadFromJsonAsync<RefundDto>(Ct))!;
        var againRefund = (await again.Content.ReadFromJsonAsync<RefundDto>(Ct))!;
        Assert.Equal(firstRefund.Id, againRefund.Id);
        Assert.Equal(12.50m, firstRefund.Amount);
    }

    [Fact]
    public async Task Refund_of_an_open_session_is_409()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);

        var refund = await RefundAsync(client, session.Id, "refund-1");

        Assert.Equal(HttpStatusCode.Conflict, refund.StatusCode);
    }
}
