using Testcontainers.Redis;

namespace NewStore.Gateway.Tests;

/// <summary>A gateway with its own Redis, the shared Keycloak, a stub for every destination and a browser. One per test.</summary>
internal sealed class GatewayHarness : IAsyncDisposable
{
    private readonly GatewayKeycloak _keycloak;
    private readonly Action<GatewayApp>? _configure;

    private GatewayHarness(GatewayKeycloak keycloak, Action<GatewayApp>? configure)
    {
        _keycloak = keycloak;
        _configure = configure;
    }

    public RedisContainer Redis { get; } = new RedisBuilder("redis:8.10.3").Build();

    public GatewayApp App { get; private set; } = null!;

    public Browser Browser { get; private set; } = null!;

    public StubHttpHandler Stub => App.Stub;

    public static async Task<GatewayHarness> StartAsync(GatewayKeycloak keycloak, Action<GatewayApp>? configure = null)
    {
        var harness = new GatewayHarness(keycloak, configure);
        await harness.Redis.StartAsync(TestContext.Current.CancellationToken);
        harness.App = harness.NewApp();
        harness.Browser = new Browser(harness.App.CreateBrowserClient());
        return harness;
    }

    /// <summary>Stops the gateway and starts a new one on the same Redis. The browser keeps its cookies.</summary>
    public async Task RestartGatewayAsync()
    {
        await App.DisposeAsync();
        App = NewApp();
        Browser.Gateway = App.CreateBrowserClient();
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Redis.DisposeAsync();
    }

    private GatewayApp NewApp()
    {
        var app = new GatewayApp(_keycloak, Redis.GetConnectionString());
        _configure?.Invoke(app);
        return app;
    }
}
