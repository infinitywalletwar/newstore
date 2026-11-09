using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public class KeycloakTests(KeycloakServer keycloak)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

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
        var client = await TestApp.StartAsync(new Dictionary<string, string?>
        {
            ["Auth:Issuer"] = KeycloakServer.PublicIssuer,
            ["Auth:MetadataAddress"] = keycloak.InternalIssuer + "/.well-known/openid-configuration",
            ["Auth:Audience"] = "inventory",
            ["Auth:RequireHttpsMetadata"] = "false",
        });

        var ordersToken = await keycloak.ClientTokenAsync("orders");
        var forCatalogOnly = await keycloak.ClientTokenAsync("inventory"); // audiences: catalog, not inventory

        Assert.Equal(HttpStatusCode.OK, (await client.WithBearer(ordersToken).GetAsync("/orders-only", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.WithBearer(forCatalogOnly).GetAsync("/inventory-only", Ct)).StatusCode);
    }

    [Fact]
    public async Task Sample_creator_has_their_catalog_id()
    {
        // The admin API takes a token from the master realm.
        using var http = new HttpClient();
        var masterToken = await http.PostAsync($"{keycloak.InternalUrl}/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = "admin",
                ["password"] = "admin",
            }), Ct);
        masterToken.EnsureSuccessStatusCode();
        var accessToken = (await masterToken.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;

        var users = await http.WithBearer(accessToken)
            .GetFromJsonAsync<JsonElement>($"{keycloak.InternalUrl}/admin/realms/newstore/users?username=orbital-kitchen&exact=true", Ct);

        Assert.Equal("00000000-0000-7000-8000-000000000001", users[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Our_clients_have_no_password_grant()
    {
        var response = await keycloak.RequestTokenAsync("password", "gateway", RealmFile.Secret("gateway"),
            ("username", "orbital-kitchen"), ("password", "orbital-kitchen"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("unauthorized_client", body.GetProperty("error").GetString());
    }
}
