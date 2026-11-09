using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public class ServiceTokenSourceTests(KeycloakServer keycloak)
{
    [Fact]
    public async Task Default_source_gets_a_client_credentials_token_and_reuses_it()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:Inventory"] = "http://inventory.test",
            ["Auth:Issuer"] = KeycloakServer.PublicIssuer,
            ["Auth:Audience"] = "orders",
            ["Auth:ClientId"] = "orders",
            ["Auth:ClientSecret"] = RealmFile.Secret("orders"),
            ["Auth:TokenEndpoint"] = keycloak.InternalIssuer + "/protocol/openid-connect/token",
        });
        builder.AddServiceClient<InventoryClient>("Services:Inventory");
        using var host = builder.Build();
        var source = host.Services.GetRequiredService<IServiceTokenSource>();

        var first = await source.GetTokenAsync(TestContext.Current.CancellationToken);
        var second = await source.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("orders", TestTokenClaims.Read(first).GetProperty("azp").GetString());
        Assert.Equal(first, second);
    }
}
