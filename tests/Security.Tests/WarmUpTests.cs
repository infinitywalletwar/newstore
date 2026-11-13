using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.HealthChecks;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Security.Tests;

public class WarmUpTests
{
    private static async Task<HealthStatus> ReadinessAsync(IHost host) =>
        (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken)).Status;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true within 10 s.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Readiness_is_unhealthy_until_the_warm_up_has_succeeded_once_and_stays_healthy()
    {
        var release = new TaskCompletionSource();
        var runs = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.AddReadinessWarmUp(async (_, _) =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
        });
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, await ReadinessAsync(host));

        release.SetResult();
        await WaitUntilAsync(() => ReadinessAsync(host).GetAwaiter().GetResult() == HealthStatus.Healthy);
        // Later checks only read the flag: the warm-up does not run again.
        await ReadinessAsync(host);
        await ReadinessAsync(host);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_failed_warm_up_is_retried_until_it_succeeds()
    {
        var runs = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.AddReadinessWarmUp(
            (_, _) => ++runs < 3 ? throw new HttpRequestException("Keycloak is starting") : Task.CompletedTask,
            firstRetryPause: TimeSpan.FromMilliseconds(10));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => ReadinessAsync(host).GetAwaiter().GetResult() == HealthStatus.Healthy);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task The_auth_warm_up_fetches_the_signing_keys_and_one_token()
    {
        var tokens = new CountingTokenSource();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IServiceTokenSource>(tokens);
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure(o =>
            o.ConfigurationManager = new FakeConfigurationManager(() => tokens.MetadataFetches++));
        using var host = builder.Build();

        await WarmUpExtensions.WarmUpAuthAsync(host.Services, TestContext.Current.CancellationToken);

        Assert.Equal(1, tokens.MetadataFetches);
        Assert.Equal(1, tokens.Fetches);
    }

    private sealed class CountingTokenSource : IServiceTokenSource
    {
        public int Fetches;
        public int MetadataFetches;

        public Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult("token");
        }
    }

    private sealed class FakeConfigurationManager(Action fetched)
        : Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>
    {
        public Task<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            fetched();
            return Task.FromResult(new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration());
        }

        public void RequestRefresh()
        {
        }
    }
}
