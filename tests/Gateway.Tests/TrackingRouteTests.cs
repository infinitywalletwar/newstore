using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NewStore.Gateway.Tests;

/// <summary>
/// Delivery's routes: two destinations in round robin, the health check that takes one out, and the hub's upgrade, which a browser
/// sends without <c>X-CSRF</c>. The test server has no upgrade feature, so YARP forwards the upgrade as a plain GET: these tests prove
/// the gateway's decisions, and the stack and Task 7's browser test prove the tunnel.
/// </summary>
public sealed class TrackingRouteTests(GatewayKeycloak keycloak)
{
    private const string Hub = "/api/hubs/tracking";
    private static readonly Guid ShipmentId = Guid.Parse("00000000-0000-7000-8000-0000000000d1");

    private static readonly string[] s_deliveryHosts = ["delivery-a.test", "delivery-b.test"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What a browser puts on a WebSocket upgrade, with <paramref name="origin"/> unless it is null.</summary>
    private static Dictionary<string, string> Upgrade(string? origin)
    {
        var headers = new Dictionary<string, string>
        {
            ["Connection"] = "Upgrade",
            ["Upgrade"] = "websocket",
            ["Sec-WebSocket-Version"] = "13",
            ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==",
        };
        if (origin is not null)
        {
            headers["Origin"] = origin;
        }

        return headers;
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString();

    /// <summary>A bearer call, which needs no sign-in: the tests that are about the destinations, not the credential.</summary>
    private static Task<HttpResponseMessage> BearerGetAsync(GatewayHarness gateway, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.User(Users.OrbitalKitchenId, "delivery"));
        return gateway.Browser.Gateway.SendAsync(request, Ct);
    }

    public static TheoryData<string> DeliveryRoutes => new() { "/api/me/shipments", $"/api/shipments/{ShipmentId}" };

    [Theory]
    [MemberData(nameof(DeliveryRoutes))]
    public async Task Delivery_routes_reach_delivery_with_the_users_token(string path)
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, path["/api".Length..], _ => Ok());
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.GetAsync(path, csrf: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Contains(forwarded.RequestUri!.Host, s_deliveryHosts);
        Assert.Equal(path["/api".Length..], forwarded.RequestUri.AbsolutePath);
        Assert.Equal("Bearer", forwarded.Headers.Authorization!.Scheme);
        Assert.False(forwarded.Headers.Contains("Cookie"), "The session cookie must stop at the gateway.");
    }

    [Fact]
    public async Task Warehouse_route_is_anonymous()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/delivery/warehouse", _ => Ok());

        var response = await gateway.Browser.GetAsync("/api/delivery/warehouse");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Equal("/delivery/warehouse", forwarded.RequestUri!.AbsolutePath);
        Assert.Null(forwarded.Headers.Authorization);
    }

    [Fact]
    public async Task Delivery_cluster_round_robins_between_its_two_destinations()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/me/shipments", _ => Ok());

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await BearerGetAsync(gateway, "/api/me/shipments")).StatusCode);
        }

        var hosts = gateway.Stub.Requests.Select(r => r.RequestUri!.Host).ToList();
        Assert.Equal(4, hosts.Count);
        Assert.Equal(s_deliveryHosts, hosts.Distinct().Order());
        Assert.All(hosts.Zip(hosts.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public async Task Unready_delivery_destination_gets_no_requests()
    {
        // Review Focus 2: one Delivery instance is stopped or not ready. The active health check takes it out, so REST calls and new
        // WebSocket connections go to the other one.
        // The replies are set before the gateway starts, so its first probes already get them.
        await using var gateway = await GatewayHarness.StartAsync(keycloak, app =>
        {
            app.Settings["Gateway:DestinationHealthChecks:Enabled"] = "true";
            app.Settings["Gateway:DestinationHealthChecks:Interval"] = "00:00:01";
            app.Stub.On(HttpMethod.Get, "/health/ready", request =>
                new HttpResponseMessage(request.RequestUri!.Host == "delivery-a.test" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            app.Stub.On(HttpMethod.Get, "/me/shipments", _ => Ok());
        });

        // ConsecutiveFailures marks a destination unhealthy at its second failed probe in a row (the default threshold), and the
        // policy runs once a round of probes has completed. A third probe to delivery-a.test means the round with the second
        // failure is over and evaluated.
        await WaitAsync(() => gateway.Stub.Requests.Count(IsProbeOf("delivery-a.test")) >= 3, "three probes of delivery-a.test");
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await BearerGetAsync(gateway, "/api/me/shipments")).StatusCode);
        }

        var calls = gateway.Stub.Requests.Where(r => r.RequestUri!.AbsolutePath != "/health/ready").ToList();
        Assert.Equal(4, calls.Count);
        Assert.All(calls, r => Assert.Equal("delivery-b.test", r.RequestUri!.Host));
        // The probes went through the same stub, to both destinations.
        Assert.True(gateway.Stub.Requests.Any(IsProbeOf("delivery-b.test")), "No probe reached delivery-b.test.");
    }

    private static Func<HttpRequestMessage, bool> IsProbeOf(string host) =>
        r => r.RequestUri!.AbsolutePath == "/health/ready" && r.RequestUri.Host == host;

    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after 10 s.</summary>
    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}.");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Hub_upgrade_with_the_gateways_origin_is_forwarded_without_the_csrf_header()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/hubs/tracking", _ => Ok());
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(HttpMethod.Get, Hub, headers: Upgrade(GatewayKeycloak.GatewayOrigin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Contains(forwarded.RequestUri!.Host, s_deliveryHosts);
        Assert.Equal("/hubs/tracking", forwarded.RequestUri.AbsolutePath);
        Assert.Equal("Bearer", forwarded.Headers.Authorization!.Scheme);
        Assert.False(forwarded.Headers.Contains("Cookie"), "The session cookie must stop at the gateway.");
    }

    [Fact]
    public async Task Hub_upgrade_from_another_origin_is_403_csrf()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        // A page on another site opens the socket; the browser adds the session cookie and its own Origin, which no script can change.
        var response = await gateway.Browser.SendAsync(HttpMethod.Get, Hub, headers: Upgrade("http://evil.test"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("urn:newstore:problem:csrf", problem.GetProperty("type").GetString());
        // Not the X-CSRF title: a browser can't add that header to a WebSocket.
        Assert.Equal("The WebSocket must come from one of the app's own origins.", problem.GetProperty("title").GetString());
        Assert.Empty(gateway.Stub.Requests);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("HTTP://LOCALHOST")]
    public async Task Hub_upgrade_origin_matches_without_a_trailing_slash_and_ignoring_case(string origin)
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/hubs/tracking", _ => Ok());
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(HttpMethod.Get, Hub, headers: Upgrade(origin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(gateway.Stub.Requests);
    }

    [Theory]
    [InlineData("http://localhost.evil.test")]
    [InlineData("http://localhost:8100.evil.test")]
    [InlineData("https://localhost")]
    public async Task Hub_upgrade_from_a_look_alike_origin_is_403_csrf(string origin)
    {
        // The whole origin must match: a host that starts like ours, or another scheme, is another origin.
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(HttpMethod.Get, Hub, headers: Upgrade(origin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:csrf", await ProblemTypeAsync(response));
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Hub_upgrade_without_an_origin_is_403_csrf()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(HttpMethod.Get, Hub, headers: Upgrade(origin: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:csrf", await ProblemTypeAsync(response));
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Plain_get_to_the_hub_without_the_csrf_header_is_still_403()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        // The right Origin, but no upgrade: an ordinary request, so the ordinary rule.
        var response = await gateway.Browser.SendAsync(
            HttpMethod.Get, Hub, headers: new Dictionary<string, string> { ["Origin"] = GatewayKeycloak.GatewayOrigin });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:csrf", await ProblemTypeAsync(response));
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Upgrade_headers_do_not_exempt_other_api_paths()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(
            HttpMethod.Get, "/api/me/shipments", headers: Upgrade(GatewayKeycloak.GatewayOrigin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:csrf", await ProblemTypeAsync(response));
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Anonymous_hub_upgrade_is_401()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.SendAsync(HttpMethod.Get, Hub, headers: Upgrade(GatewayKeycloak.GatewayOrigin));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Gateway_refuses_to_start_without_public_origins()
    {
        // The gateway fails on start, before it connects to Redis, so the address needs no server.
        await using var app = new GatewayApp(keycloak, "127.0.0.1:1");
        app.Settings.Remove("Gateway:PublicOrigins:0");

        var refused = Assert.ThrowsAny<Exception>(() => app.Services);

        Assert.Contains("Gateway:PublicOrigins", refused.ToString());
    }
}
