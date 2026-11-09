using System.Net;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public class PolicyTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignedIn_rejects_a_service_token()
    {
        var client = (await TestApp.StartWithTestKeyAsync()).WithBearer(TestTokens.Client("orders", "inventory"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task SignedIn_accepts_a_user_token_from_the_gateway()
    {
        var userId = Guid.CreateVersion7();
        var client = (await TestApp.StartWithTestKeyAsync()).WithBearer(TestTokens.User(userId, "inventory"));

        var response = await client.GetAsync("/me", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{userId}\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SignedIn_rejects_a_user_token_from_an_unknown_client()
    {
        var client = (await TestApp.StartWithTestKeyAsync()).WithBearer(TestTokens.User(Guid.CreateVersion7(), "inventory", clientId: "mobile"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Client_policy_accepts_only_the_named_client()
    {
        var client = await TestApp.StartWithTestKeyAsync();

        client.WithBearer(TestTokens.Client("orders", "inventory"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orders-only", Ct)).StatusCode);

        client.WithBearer(TestTokens.Client("inventory", "inventory"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/orders-only", Ct)).StatusCode);
    }

    [Fact]
    public async Task Wrong_audience_is_401()
    {
        var client = (await TestApp.StartWithTestKeyAsync(audience: "inventory")).WithBearer(TestTokens.Client("orders", "payments"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/orders-only", Ct)).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var token = TestTokens.User(Guid.CreateVersion7(), "inventory", lifetime: TimeSpan.FromMinutes(-10));
        var client = (await TestApp.StartWithTestKeyAsync()).WithBearer(token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me", Ct)).StatusCode);
    }
}
