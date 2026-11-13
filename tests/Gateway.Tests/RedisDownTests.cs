using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewStore.Gateway.Tests;

public sealed class RedisDownTests(GatewayKeycloak keycloak)
{
    [Fact]
    public async Task Anonymous_browsing_works_when_redis_is_down()
    {
        // The ticket store logs a warning with a stack trace for each failed read. Collect it instead of printing it, and assert it.
        var logs = new LogCollector();
        await using var gateway = await GatewayHarness.StartAsync(keycloak, app =>
            app.TestServices.Add(services => services.AddLogging(logging => logging.ClearProviders().AddProvider(logs))));
        gateway.Stub.On(HttpMethod.Get, "/videos", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        // A signed-in browser, so the cookie is still sent when Redis stops: its ticket cannot be read.
        await gateway.Browser.SignInAsync(Users.OrbitalKitchen);

        await gateway.Redis.StopAsync(TestContext.Current.CancellationToken);

        var videos = await gateway.Browser.GetAsync("/api/videos");
        Assert.Equal(HttpStatusCode.OK, videos.StatusCode);
        Assert.Null(Assert.Single(gateway.Stub.Requests).Headers.Authorization);

        var user = await gateway.Browser.GetAsync("/auth/user", csrf: true);
        var body = await user.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(body.GetProperty("isAuthenticated").GetBoolean());

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Redis failed while reading a session", StringComparison.Ordinal));

        var ready = await gateway.Browser.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
