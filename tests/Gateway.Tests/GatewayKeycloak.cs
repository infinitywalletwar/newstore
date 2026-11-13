using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Testcontainers.Keycloak;

namespace NewStore.Gateway.Tests;

/// <summary>
/// One Keycloak container per test assembly, with the newstore realm imported and no hostname set, so the issuer is the URL
/// the container is reached at. The test and the gateway both use that URL. The host is 127.0.0.1, not localhost: the gateway
/// is <c>http://localhost</c> and the test's one cookie container must not mix the two servers' cookies (cookies are not
/// isolated by port, RFC 6265 section 8.5).
/// </summary>
public sealed class GatewayKeycloak : IAsyncLifetime
{
    public const string RealmName = "newstore";

    /// <summary>The gateway runs in the test server at this address, so Keycloak must accept it as the redirect URI.</summary>
    public const string GatewayOrigin = "http://localhost";

    private readonly KeycloakContainer _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.8.2")
        .WithResourceMapping(new FileInfo(RealmFile.Path), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    /// <summary>For example http://127.0.0.1:55123.</summary>
    public string BaseUrl => new UriBuilder(_container.GetBaseAddress()) { Host = "127.0.0.1" }.Uri.GetLeftPart(UriPartial.Authority);

    public string Issuer => $"{BaseUrl}/realms/{RealmName}";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await AllowTheTestGatewayAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Ends every session of a user, as an administrator would. The user's refresh tokens stop working.</summary>
    public async Task EndSessionsAsync(Guid userId)
    {
        using var http = await AdminClientAsync();
        var response = await http.PostAsync($"{BaseUrl}/admin/realms/{RealmName}/users/{userId}/logout", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // The realm file lists the gateway's real addresses (localhost:8100 and so on). The test gateway has none of them.
    private async Task AllowTheTestGatewayAsync()
    {
        using var http = await AdminClientAsync();
        var clients = $"{BaseUrl}/admin/realms/{RealmName}/clients";
        var found = await http.GetFromJsonAsync<JsonArray>($"{clients}?clientId=gateway", TestContext.Current.CancellationToken);
        var client = found![0]!.AsObject();
        client["redirectUris"]!.AsArray().Add($"{GatewayOrigin}/signin-oidc");
        var attributes = client["attributes"]!.AsObject();
        attributes["post.logout.redirect.uris"] = $"{attributes["post.logout.redirect.uris"]}##{GatewayOrigin}/";
        var response = await http.PutAsJsonAsync($"{clients}/{client["id"]}", client, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        using var login = new HttpClient();
        var response = await login.PostAsync($"{BaseUrl}/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = KeycloakBuilder.DefaultUsername,
                ["password"] = KeycloakBuilder.DefaultPassword,
            }), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("access_token").GetString());
        return http;
    }
}
