using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Gateway.Tests;

/// <summary>What the gateway forwards, to where, and with which credentials. Destinations are a stub that records requests.</summary>
public sealed class ProxyTests(GatewayKeycloak keycloak)
{
    private static readonly Guid EditionId = Guid.Parse("00000000-0000-7000-8000-0000000000e1");
    private static readonly Guid UploadId = Guid.Parse("00000000-0000-7000-8000-0000000000a1");

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Signed_in_api_call_reaches_the_service_with_the_users_access_token()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/editions/{id}", _ => Json("{}"));
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.GetAsync($"/api/editions/{EditionId}", csrf: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Equal($"http://inventory.test/editions/{EditionId}", forwarded.RequestUri!.ToString());
        Assert.Equal("Bearer", forwarded.Headers.Authorization!.Scheme);
        var token = new JsonWebTokenHandler().ReadJsonWebToken(forwarded.Headers.Authorization.Parameter);
        Assert.Equal(Users.OrbitalKitchenId.ToString(), token.GetClaim("sub").Value);
        Assert.False(forwarded.Headers.Contains("Cookie"), "The session cookie must stop at the gateway.");
    }

    [Fact]
    public async Task Signed_in_user_who_fails_the_routes_policy_gets_403_and_not_a_redirect()
    {
        // Every real route asks only for "signed in", and the user always passes. The test makes the policy ask for a claim the
        // user lacks, so the cookie scheme's forbid path (OnRedirectToAccessDenied) is the one that answers.
        await using var gateway = await GatewayHarness.StartAsync(keycloak, app => app.TestServices.Add(services =>
            services.Configure<AuthorizationOptions>(o => o.AddPolicy(
                Policies.SignedIn, policy => policy.RequireAuthenticatedUser().RequireClaim("role", "admin")))));
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.GetAsync($"/api/editions/{EditionId}", csrf: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("urn:newstore:problem:forbidden", problem.GetProperty("type").GetString());
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Cookie_request_without_the_csrf_header_is_403_and_not_forwarded()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(HttpMethod.Post, "/api/orders", csrf: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("urn:newstore:problem:csrf", problem.GetProperty("type").GetString());
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Bearer_request_is_forwarded_as_is_without_csrf()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/editions/{id}", _ => Json("{}"));
        var token = TestTokens.User(Users.OrbitalKitchenId, "inventory");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/editions/{EditionId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await gateway.Browser.Gateway.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Equal(token, forwarded.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Bearer_token_of_a_service_client_is_rejected()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/editions/{EditionId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Client("orders", "inventory"));

        var response = await gateway.Browser.Gateway.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Anonymous_read_is_forwarded_without_a_token()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/videos", _ => Json("""{"total":0}"""));

        var response = await gateway.Browser.GetAsync("/api/videos?q=sourdough");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Equal("http://catalog.test/videos?q=sourdough", forwarded.RequestUri!.ToString());
        Assert.Null(forwarded.Headers.Authorization);
    }

    [Fact]
    public async Task Other_authorization_headers_are_not_forwarded()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        gateway.Stub.On(HttpMethod.Get, "/videos", _ => Json("[]"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/videos");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");

        var response = await gateway.Browser.Gateway.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(gateway.Stub.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task Anonymous_call_to_a_signed_in_route_is_401_not_a_redirect()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.SendAsync(HttpMethod.Post, "/api/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(gateway.Stub.Requests);
    }

    public static TheoryData<string, string, string> NewRoutes => new()
    {
        { "POST", "/api/videos", "catalog.test" },
        { "GET", "/api/me/videos", "catalog.test" },
        { "POST", "/api/uploads", "media.test" },
        { "GET", $"/api/uploads/{UploadId}", "media.test" },
        { "POST", $"/api/uploads/{UploadId}/complete", "media.test" },
        { "DELETE", $"/api/uploads/{UploadId}", "media.test" },
        { "GET", "/api/me/uploads", "media.test" },
        { "POST", "/api/downloads", "media.test" },
    };

    [Theory]
    [MemberData(nameof(NewRoutes))]
    public async Task New_routes_reach_their_service_with_the_users_token(string method, string path, string host)
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        var verb = new HttpMethod(method);
        gateway.Stub.On(verb, path["/api".Length..], _ => Json("{}"));
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        var response = await gateway.Browser.SendAsync(verb, path, csrf: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(gateway.Stub.Requests);
        Assert.Equal($"http://{host}{path["/api".Length..]}", forwarded.RequestUri!.ToString());
        Assert.Equal("Bearer", forwarded.Headers.Authorization!.Scheme);
        Assert.False(forwarded.Headers.Contains("Cookie"), "The session cookie must stop at the gateway.");
    }

    public static TheoryData<string, string> NewRoutePaths
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var row in NewRoutes)
            {
                data.Add(row.Data.Item1, row.Data.Item2);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NewRoutePaths))]
    public async Task New_routes_need_a_signed_in_user(string method, string path)
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.SendAsync(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(gateway.Stub.Requests);
    }

    [Theory]
    [InlineData("GET", "/api/health/ready")]
    [InlineData("GET", "/api/openapi/v1.json")]
    [InlineData("GET", "/api/editions/00000000-0000-7000-8000-0000000000e1/reservations")]
    [InlineData("POST", "/api/reservations/00000000-0000-7000-8000-0000000000e1/confirm")]
    [InlineData("GET", "/api/videos/00000000-0000-7000-8000-0000000000e1/owner")]
    [InlineData("PUT", "/api/uploads/00000000-0000-7000-8000-0000000000a1")]
    [InlineData("GET", "/api/downloads")]
    public async Task Internal_paths_are_not_routed(string method, string path)
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        // A signed-in caller, so a missing route cannot hide behind a 401.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.User(Users.OrbitalKitchenId, "inventory"));

        var response = await gateway.Browser.Gateway.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(gateway.Stub.Requests);
    }

    [Fact]
    public async Task Unknown_non_api_path_serves_the_app()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.GetAsync("/videos/123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<title>newstore</title>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_api_path_is_404_problem_not_the_app()
    {
        await using var gateway = await GatewayHarness.StartAsync(keycloak);

        var response = await gateway.Browser.GetAsync("/api/no-such-thing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
