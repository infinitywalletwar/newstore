using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public sealed class InventoryClient(HttpClient http)
{
    public Uri? BaseAddress => http.BaseAddress;

    public Task<HttpResponseMessage> PingAsync(CancellationToken cancellationToken) => http.GetAsync("/ping", cancellationToken);
}

public class ServiceClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static IHost StartHost(StubHttpHandler stub)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:Inventory"] = "http://inventory.test",
            ["Auth:Issuer"] = TestTokens.Issuer,
            ["Auth:Audience"] = "orders",
            ["Auth:ClientId"] = "orders",
        });
        builder.AddServiceClient<InventoryClient>("Services:Inventory");
        builder.Services.UseTestServiceTokens();
        builder.Services.UseStubHandler<InventoryClient>(stub);
        // The standard pipeline waits seconds between attempts; the test does not need to.
        builder.Services.ConfigureAll<HttpStandardResilienceOptions>(o =>
        {
            o.Retry.Delay = TimeSpan.Zero;
            o.Retry.UseJitter = false;
        });
        return builder.Build();
    }

    [Fact]
    public async Task Service_client_sends_a_token_on_every_retry()
    {
        var calls = 0;
        var stub = new StubHttpHandler().On(HttpMethod.Get, "/ping", _ =>
            new HttpResponseMessage(++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var host = StartHost(stub);

        var response = await host.Services.GetRequiredService<InventoryClient>().PingAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Equal("Bearer", r.Headers.Authorization?.Scheme));
        // The test token source returns a new token on each call, so two different tokens prove the token handler ran per attempt.
        Assert.Equal(2, stub.Requests.Select(r => r.Headers.Authorization?.Parameter).Distinct().Count());
    }

    [Fact]
    public void Service_client_reads_its_base_address_from_configuration()
    {
        using var host = StartHost(new StubHttpHandler());

        Assert.Equal(new Uri("http://inventory.test"), host.Services.GetRequiredService<InventoryClient>().BaseAddress);
    }
}
