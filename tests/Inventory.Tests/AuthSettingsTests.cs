using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Inventory.Tests;

public class AuthSettingsTests(PostgresServer postgres, KafkaServer kafka)
{
    // NewStoreAppFactory adds "gateway" to Auth:UserClients, which hides a missing line in appsettings.json.
    // Here the default is removed, so the list must come from the file, as it does in Compose.
    [Fact]
    public async Task Appsettings_names_the_gateway_as_a_user_client_and_inventory_as_the_audience()
    {
        await using var factory = new NewStoreAppFactory<Program>();
        factory.Settings["ConnectionStrings:Inventory"] =
            await postgres.CreateDatabaseAsync("inventory", TestContext.Current.CancellationToken);
        factory.Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        factory.Settings.Remove("Auth:UserClients:0");

        var auth = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

        Assert.Equal(["gateway"], auth.UserClients);
        Assert.Equal("inventory", auth.Audience);
    }
}
