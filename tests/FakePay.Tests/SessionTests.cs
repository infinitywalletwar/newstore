using System.Net.Http.Json;

namespace NewStore.FakePay.Tests;

public class SessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_session()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();

        var first = await app.CreateSessionAsync(client, idempotencyKey: "key-1");
        var again = await app.CreateSessionAsync(client, idempotencyKey: "key-1");
        var other = await app.CreateSessionAsync(client, idempotencyKey: "key-2");

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal($"http://fakepay.test/checkout/{first.Id}", first.Url);
        Assert.Equal("open", first.Status);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(23 * 60, true)]
    [InlineData(25 * 60, false)]
    public async Task Session_lifetime_outside_30_minutes_to_24_hours_is_400(int minutes, bool accepted)
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();

        var response = await app.PostSessionAsync(client, Guid.NewGuid().ToString(), TimeSpan.FromMinutes(minutes));

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_or_wrong_api_key_is_401()
    {
        await using var app = new FakePayApp();
        using var anonymous = app.CreateApiClient(apiKey: null);
        using var wrong = app.CreateApiClient(apiKey: "not-the-key");

        var missing = await app.PostSessionAsync(anonymous, "k", TimeSpan.FromMinutes(30));
        var mismatch = await app.PostSessionAsync(wrong, "k", TimeSpan.FromMinutes(30));
        var refund = await wrong.PostAsJsonAsync("/v1/refunds", new { sessionId = "cs_x" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refund.StatusCode);
    }

    [Fact]
    public async Task Open_session_expires_and_sends_an_expired_webhook()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);

        // The sweeper's timer runs on the fake clock: moving it past expiresAt makes the sweeper tick.
        app.Clock.Advance(TimeSpan.FromMinutes(31));

        var webhook = Assert.Single(await app.WaitForWebhooksAsync(1));
        Assert.Equal("checkout.session.expired", webhook.Type);
        Assert.Equal(session.Id, webhook.SessionId);

        var pay = await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, pay.StatusCode);
    }
}
