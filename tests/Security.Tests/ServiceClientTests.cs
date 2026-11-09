using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public sealed class InventoryClient(HttpClient http)
{
    public Uri? BaseAddress => http.BaseAddress;

    public Task<HttpResponseMessage> PingAsync(CancellationToken cancellationToken) => http.GetAsync("/ping", cancellationToken);
}

public class ServiceClientTests
{
    private static readonly Dictionary<string, string?> CallerSettings = new()
    {
        ["Services:Inventory"] = "http://inventory.test",
        ["Auth:Issuer"] = TestTokens.Issuer,
        ["Auth:Audience"] = "orders",
        ["Auth:ClientId"] = "orders",
        ["Auth:ClientSecret"] = "orders",
        ["Auth:TokenEndpoint"] = "http://keycloak.test/token",
        ["Auth:UserClients:0"] = "gateway",
    };

    private static IHost StartHost(StubHttpHandler stub, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(CallerSettings);
        builder.AddServiceClient<InventoryClient>("Services:Inventory");
        builder.Services.UseTestServiceTokens();
        configure?.Invoke(builder.Services);
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

        var response = await host.Services.GetRequiredService<InventoryClient>().PingAsync(TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task A_failed_token_fetch_is_retried_like_any_transient_failure()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, "/ping", _ => new HttpResponseMessage(HttpStatusCode.OK));
        var tokenCalls = 0;
        using var host = StartHost(stub, services => services.AddSingleton<IServiceTokenSource>(new FlakyTokenSource(() =>
            ++tokenCalls == 1 ? throw new HttpRequestException("Keycloak is restarting") : "token")));

        var response = await host.Services.GetRequiredService<InventoryClient>().PingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, tokenCalls);
        Assert.Single(stub.Requests); // the first attempt failed before anything was sent
    }

    [Fact]
    public async Task A_caller_without_client_settings_fails_at_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Services:Inventory"] = "http://inventory.test" });
        builder.AddServiceClient<InventoryClient>("Services:Inventory");
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Auth:ClientId", failure.Message);
    }

    [Fact]
    public void Auth_settings_are_bound_once_when_a_service_validates_tokens_and_calls_others()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(CallerSettings);
        builder.AddNewStoreAuth();
        builder.AddServiceClient<InventoryClient>("Services:Inventory");
        builder.AddServiceClient<OtherClient>("Services:Inventory");
        using var host = builder.Build();

        Assert.Equal(["gateway"], host.Services.GetRequiredService<IOptions<AuthOptions>>().Value.UserClients);
    }

    private sealed class FlakyTokenSource(Func<string> next) : IServiceTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(next());
    }
}

public sealed class OtherClient(HttpClient http)
{
    public HttpClient Http => http;
}
