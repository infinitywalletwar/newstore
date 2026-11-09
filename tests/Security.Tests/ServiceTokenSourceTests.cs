using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public class ServiceTokenSourceTests(KeycloakServer keycloak)
{
    private IServiceTokenSource CreateSource(string secret, out IHost host)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:Inventory"] = "http://inventory.test",
            ["Auth:ClientId"] = "orders",
            ["Auth:ClientSecret"] = secret,
            ["Auth:TokenEndpoint"] = keycloak.InternalIssuer + "/protocol/openid-connect/token",
        });
        builder.AddServiceClient<InventoryClient>("Services:Inventory");
        host = builder.Build();
        return host.Services.GetRequiredService<IServiceTokenSource>();
    }

    [Fact]
    public async Task A_failed_token_request_throws_HttpRequestException()
    {
        var source = CreateSource("not-the-secret", out var host);
        using var _ = host;

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Default_source_gets_a_client_credentials_token_and_reuses_it()
    {
        var source = CreateSource(RealmFile.Secret("orders"), out var host);
        using var _ = host;

        var first = await source.GetTokenAsync(TestContext.Current.CancellationToken);
        var second = await source.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("orders", TestTokenClaims.Read(first).GetProperty("azp").GetString());
        Assert.Equal(first, second);
    }
}
