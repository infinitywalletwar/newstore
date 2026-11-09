using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NewStore.Security.Tests;

public class KeycloakTests(KeycloakServer keycloak)
{
    [Fact]
    public async Task Token_from_the_internal_url_carries_the_public_issuer()
    {
        var claims = TestTokenClaims.Read(await keycloak.ClientTokenAsync("orders"));

        Assert.Equal(KeycloakServer.PublicIssuer, claims.GetProperty("iss").GetString());
        var audiences = claims.GetProperty("aud").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("inventory", audiences);
        Assert.Contains("payments", audiences);
        Assert.Equal("orders", claims.GetProperty("azp").GetString());
    }

    [Fact]
    public async Task A_service_validates_a_keycloak_token_with_the_public_issuer()
    {
        await using var app = await TestApp.StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = KeycloakServer.PublicIssuer,
            ["Auth:MetadataAddress"] = keycloak.InternalIssuer + "/.well-known/openid-configuration",
            ["Auth:Audience"] = "inventory",
            ["Auth:RequireHttpsMetadata"] = "false", // the metadata address is a plain-http container URL
        });
        var ct = TestContext.Current.CancellationToken;

        var ordersToken = await keycloak.ClientTokenAsync("orders");
        var forCatalogOnly = await keycloak.ClientTokenAsync("inventory"); // audiences: catalog, not inventory

        Assert.Equal(HttpStatusCode.OK, (await app.Client.WithBearer(ordersToken).GetAsync("/orders-only", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.WithBearer(forCatalogOnly).GetAsync("/inventory-only", ct)).StatusCode);
    }

    [Fact]
    public async Task Sample_creator_has_their_catalog_id()
    {
        using var http = new HttpClient();
        http.WithBearer(await keycloak.AdminTokenAsync());

        var users = await http.GetFromJsonAsync<JsonElement>(
            $"{keycloak.InternalUrl}/admin/realms/newstore/users?username=orbital-kitchen&exact=true", TestContext.Current.CancellationToken);

        Assert.Equal("00000000-0000-7000-8000-000000000001", users[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("orders")]
    [InlineData("inventory")]
    public async Task Our_clients_have_no_password_grant(string clientId)
    {
        var response = await keycloak.RequestTokenAsync("password", clientId, RealmFile.Secret(clientId),
            ("username", "orbital-kitchen"), ("password", RealmFile.UserPassword("orbital-kitchen")));

        await AssertUnauthorizedClientAsync(response);
    }

    [Fact]
    public async Task The_gateway_has_no_service_account()
    {
        var response = await keycloak.RequestTokenAsync("client_credentials", "gateway", RealmFile.Secret("gateway"));

        // Keycloak answers this one with 401, not 400.
        await AssertUnauthorizedClientAsync(response, HttpStatusCode.Unauthorized);
    }

    private static async Task AssertUnauthorizedClientAsync(
        HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("unauthorized_client", body.GetProperty("error").GetString());
    }
}
