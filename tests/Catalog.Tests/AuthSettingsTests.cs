using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Catalog.Tests;

public class AuthSettingsTests(KafkaServer kafka)
{
    // NewStoreAppFactory adds "gateway" to Auth:UserClients, which hides a missing line in appsettings.json.
    // Here the default is removed, so the list must come from the file, as it does in Compose.
    [Fact]
    public async Task Appsettings_names_the_gateway_as_a_user_client_and_catalog_as_the_audience()
    {
        await using var factory = new NewStoreAppFactory<Program>
        {
            Settings = { ["ConnectionStrings:Catalog"] = "Host=127.0.0.1;Port=1;Database=catalog;Username=catalog;Password=catalog;Timeout=2" },
        };
        factory.UseKafka(kafka);
        factory.Settings.Remove("Auth:UserClients:0");

        var auth = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

        Assert.Equal(["gateway"], auth.UserClients);
        Assert.Equal("catalog", auth.Audience);
    }
}
