using System.Net.Http.Json;
using System.Text.Json;

using Testcontainers.Keycloak;

namespace NewStore.Security.Tests;

/// <summary>
/// One Keycloak container per test assembly, with the newstore realm imported. Its public hostname is
/// <see cref="PublicUrl"/>, which the test cannot reach: the tests call the mapped URL (the back channel), as a container would.
/// </summary>
public sealed class KeycloakServer : IAsyncLifetime
{
    public const string PublicUrl = "http://public.newstore.test:8180";
    public const string PublicIssuer = PublicUrl + "/realms/newstore";

    private readonly KeycloakContainer _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.8.2")
        .WithUsername("admin")
        .WithPassword("admin")
        .WithResourceMapping(new FileInfo(RealmFile.Path), "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .WithEnvironment("KC_HOSTNAME", PublicUrl)
        .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
        .Build();

    /// <summary>The mapped URL on this machine, for example http://localhost:55123.</summary>
    public string InternalUrl => _container.GetBaseAddress().TrimEnd('/');

    public string InternalIssuer => InternalUrl + "/realms/newstore";

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<HttpResponseMessage> RequestTokenAsync(string grantType, string clientId, string? secret, params (string Key, string Value)[] more)
    {
        using var http = new HttpClient();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = clientId,
        };
        if (secret is not null)
        {
            form["client_secret"] = secret;
        }

        foreach (var (key, value) in more)
        {
            form[key] = value;
        }

        return await http.PostAsync($"{InternalIssuer}/protocol/openid-connect/token", new FormUrlEncodedContent(form),
            TestContext.Current.CancellationToken);
    }

    public async Task<string> ClientTokenAsync(string clientId)
    {
        var response = await RequestTokenAsync("client_credentials", clientId, RealmFile.Secret(clientId));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("access_token").GetString()!;
    }
}
