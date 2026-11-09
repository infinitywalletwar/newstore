using System.Net;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public class PolicyTests
{
    [Fact]
    public async Task SignedIn_rejects_a_service_token()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(TestTokens.Client("orders", "inventory"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task SignedIn_accepts_a_user_token_from_the_gateway()
    {
        var userId = Guid.CreateVersion7();
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(TestTokens.User(userId, "inventory"));

        var response = await client.GetAsync("/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{userId}\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignedIn_rejects_a_user_token_from_an_unknown_client()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(TestTokens.User(Guid.CreateVersion7(), "inventory", clientId: "mobile"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task SignedIn_rejects_a_token_without_a_subject()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(ForgedToken.Create(subject: null));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Client_policy_accepts_only_the_named_client()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client;

        client.WithBearer(TestTokens.Client("orders", "inventory"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/orders-only", TestContext.Current.CancellationToken)).StatusCode);

        client.WithBearer(TestTokens.Client("inventory", "inventory"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/orders-only", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Client_policy_rejects_a_user_token_from_the_gateway()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(TestTokens.User(Guid.CreateVersion7(), "inventory"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/orders-only", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Wrong_audience_is_401()
    {
        await using var app = await TestApp.StartWithTestKeyAsync(audience: "inventory");
        var client = app.Client.WithBearer(TestTokens.Client("orders", "payments"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/orders-only", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var token = TestTokens.User(Guid.CreateVersion7(), "inventory", lifetime: TimeSpan.FromMinutes(-10));
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Wrong_issuer_is_401()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(ForgedToken.Create(issuer: "http://evil.test/realms/newstore"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Wrong_signing_key_is_401()
    {
        await using var app = await TestApp.StartWithTestKeyAsync();
        var client = app.Client.WithBearer(ForgedToken.Create(key: ForgedToken.OtherKeyWithTheSameId()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me", TestContext.Current.CancellationToken)).StatusCode);
    }
}
