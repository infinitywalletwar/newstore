using System.Net;
using System.Text.Json;

using Duende.AccessTokenManagement.OpenIdConnect;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

using StackExchange.Redis;

namespace NewStore.Gateway.Tests;

/// <summary>The browser's sign-in: the session cookie, /auth/user, login and logout, and what happens when the session ends.</summary>
public sealed class SignInTests(GatewayKeycloak keycloak)
{
    private static readonly Guid EditionId = Guid.Parse("00000000-0000-7000-8000-0000000000e1");

    [Fact]
    public async Task Sign_in_sets_only_an_httponly_session_cookie()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/editions/{id}", _ => new HttpResponseMessage(HttpStatusCode.OK));
        var landing = await gateway.Browser.SignInAsync(Users.OrbitalKitchen);
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);

        var cookie = Assert.Single(gateway.Browser.GatewayCookies);
        Assert.Equal("newstore-session", cookie.Name);
        Assert.True(cookie.HttpOnly);
        // CookieContainer does not model SameSite, so read the header the gateway sent.
        var setCookie = gateway.Browser.GatewaySetCookies.Last(c => c.StartsWith("newstore-session=", StringComparison.Ordinal));
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        // No Secure flag until Phase 4, on the session cookie and on the handler's correlation and nonce cookies (ADR 0015).
        Assert.NotEmpty(gateway.Browser.GatewaySetCookies);
        Assert.DoesNotContain(gateway.Browser.GatewaySetCookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(gateway.Browser.GatewaySetCookies, c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal)
            && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(gateway.Browser.GatewaySetCookies, c => c.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal)
            && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));

        // The tokens stay on the server: make a call so the access token is known, then look for it in everything the gateway said.
        await gateway.Browser.GetAsync($"/api/editions/{EditionId}", csrf: true);
        var accessToken = Assert.Single(gateway.Stub.Requests).Headers.Authorization!.Parameter!;
        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        Assert.DoesNotContain(gateway.Browser.GatewayTranscript, text => text.Contains(accessToken, StringComparison.Ordinal));
        Assert.DoesNotContain(gateway.Browser.GatewayCookies, c => c.Value.Contains(accessToken, StringComparison.Ordinal));

        var body = await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal(Users.OrbitalKitchenId.ToString(), body.GetProperty("sub").GetString());
        Assert.Equal("Orbital Kitchen", body.GetProperty("name").GetString());
        Assert.StartsWith("/auth/logout?sid=", body.GetProperty("logoutUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(["isAuthenticated", "sub", "name", "logoutUrl"], body.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Auth_user_says_not_authenticated_without_a_session()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.GetAsync("/auth/user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("isAuthenticated").GetBoolean());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    public async Task Login_refuses_a_return_url_to_another_site(string returnUrl)
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.GetAsync($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_accepts_a_local_return_url()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.GetAsync("/auth/login?returnUrl=%2Fvideos%2F123");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(keycloak.Issuer, response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_needs_the_session_id()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var missing = await gateway.Browser.GetAsync("/auth/logout");
        var wrong = await gateway.Browser.GetAsync("/auth/logout?sid=not-my-session");

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        var body = await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task Logout_with_the_session_id_ends_the_session_and_goes_to_keycloak()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);
        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        var logoutUrl = (await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("logoutUrl").GetString()!;

        var logout = await gateway.Browser.GetAsync(logoutUrl);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains("/protocol/openid-connect/logout", logout.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("id_token_hint=", logout.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.Contains("post_logout_redirect_uri=http%3A%2F%2Flocalhost%2F", logout.Headers.Location.ToString(), StringComparison.Ordinal);
        var after = await gateway.Browser.FollowAsync(logout, logout.RequestMessage!.RequestUri!);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        // Keycloak adds a state parameter to the post-logout redirect, so compare the address without the query.
        Assert.Equal(GatewayKeycloak.GatewayOrigin + "/", after.RequestMessage!.RequestUri!.GetLeftPart(UriPartial.Path));
        Assert.Empty(gateway.Browser.GatewayCookies);
    }

    [Fact]
    public async Task Expired_session_is_401_session_expired()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak, RefreshOnEveryCall);
        await gateway.Browser.SignInAsync(Users.TrailNotes);
        await keycloak.EndSessionsAsync(Users.TrailNotesId);

        var response = await gateway.Browser.GetAsync($"/api/editions/{EditionId}", csrf: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("urn:newstore:problem:session-expired", problem.GetProperty("type").GetString());
        Assert.Empty(gateway.Stub.Requests);
        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        var body = await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task Session_survives_a_gateway_restart()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        await gateway.RestartGatewayAsync();

        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        var body = await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("isAuthenticated").GetBoolean());

        // The restarted gateway could read the cookie because the key ring is shared, so it must be in Redis and not in a file.
        await using var redis = await ConnectionMultiplexer.ConnectAsync(gateway.Redis.GetConnectionString());
        Assert.True(await redis.GetDatabase().KeyExistsAsync("newstore:data-protection-keys"));
    }

    [Fact]
    public async Task Refresh_gives_the_next_call_a_newer_token_and_is_written_back_to_the_session()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak, RefreshOnEveryCall);
        gateway.Stub.On(HttpMethod.Get, "/editions/{id}", _ => new HttpResponseMessage(HttpStatusCode.OK));
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var first = await gateway.Browser.GetAsync($"/api/editions/{EditionId}", csrf: true);
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken); // iat has a resolution of one second
        var second = await gateway.Browser.GetAsync($"/api/editions/{EditionId}", csrf: true);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var tokens = gateway.Stub.Requests.Select(r => new JsonWebTokenHandler().ReadJsonWebToken(r.Headers.Authorization!.Parameter)).ToList();
        Assert.Equal(2, tokens.Count);
        Assert.NotEqual(tokens[0].EncodedToken, tokens[1].EncodedToken);
        Assert.True(tokens[1].IssuedAt > tokens[0].IssuedAt, "The second token should have been issued by a refresh, after the first.");

        // The refreshed tokens were written back: the ticket in Redis holds the token the service got.
        var stored = await ReadStoredAccessTokenAsync(gateway.Redis.GetConnectionString());
        Assert.Equal(tokens[1].EncodedToken, stored);
    }

    private static void RefreshOnEveryCall(GatewayApp app) =>
        // Longer than the access token lives, so every call has to refresh it.
        app.TestServices.Add(services => services.Configure<UserTokenManagementOptions>(o => o.RefreshBeforeExpiration = TimeSpan.FromHours(1)));

    private static async Task<string?> ReadStoredAccessTokenAsync(string redisConnectionString)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var key = Assert.Single(redis.GetServer(redis.GetEndPoints().First()).Keys(pattern: "newstore:session:*").ToList());
        var bytes = (byte[]?)await redis.GetDatabase().StringGetAsync(key);
        return TicketSerializer.Default.Deserialize(bytes!)!.Properties.GetTokenValue("access_token");
    }

    [Fact]
    public async Task Logout_deletes_the_session_so_the_old_cookie_no_longer_works()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);
        var oldCookie = Assert.Single(gateway.Browser.GatewayCookies);
        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        var logoutUrl = (await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("logoutUrl").GetString()!;
        await gateway.Browser.GetAsync(logoutUrl);

        // A copy of the cookie from before the logout, sent by hand.
        using var replay = new HttpRequestMessage(HttpMethod.Get, "/auth/user");
        replay.Headers.Add("Cookie", $"{oldCookie.Name}={oldCookie.Value}");
        replay.Headers.Add("X-CSRF", "1");
        var response = await gateway.Browser.Gateway.SendAsync(replay, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task Logout_without_a_session_goes_to_the_start_page()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.GetAsync("/auth/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Cookie_request_to_auth_user_without_the_csrf_header_is_403()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.GetAsync("/auth/user", csrf: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
