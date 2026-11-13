using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Security;

namespace NewStore.ServiceDefaults.HealthChecks;

public static class WarmUpExtensions
{
    /// <summary>Set to <c>false</c> to skip the warm-up, for example in a test that has no Keycloak. Default: true.</summary>
    public const string EnabledKey = "WarmUp:Enabled";

    /// <summary>
    /// Runs <paramref name="warmUp"/> once in the background after startup, retrying with a growing pause (from
    /// <paramref name="firstRetryPause"/> up to 10 s) until it succeeds, and keeps <c>/health/ready</c> unhealthy until then.
    /// After the first success readiness no longer depends on it: a later Keycloak or Inventory outage does not make the service
    /// unready (the check only reads a flag), so the warm-up is a gate at startup and nothing more.
    /// </summary>
    public static IHostApplicationBuilder AddReadinessWarmUp(
        this IHostApplicationBuilder builder, Func<IServiceProvider, CancellationToken, Task> warmUp, TimeSpan? firstRetryPause = null)
    {
        if (!builder.Configuration.GetValue(EnabledKey, true))
        {
            return builder;
        }

        var state = new WarmUpState();
        builder.Services.AddSingleton(state);
        builder.Services.AddHostedService(services => new WarmUpService(
            state, warmUp, services, services.GetRequiredService<ILogger<WarmUpService>>(), firstRetryPause ?? TimeSpan.FromSeconds(1)));
        builder.Services.AddHealthChecks().AddCheck("warm-up", () => state.Done
            ? HealthCheckResult.Healthy("Warmed up.")
            : HealthCheckResult.Unhealthy("Warming up: keys, token and connections are not ready yet."));
        return builder;
    }

    /// <summary>
    /// The first-use work of validating a user's token and of calling another service: the signing keys (the OpenID metadata and the
    /// JWKS, which JwtBearer fetches on the first request) and the service's own client-credentials token.
    /// </summary>
    public static async Task WarmUpAuthAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var jwt = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        if (jwt.ConfigurationManager is { } manager)
        {
            await manager.GetConfigurationAsync(cancellationToken);
        }

        await services.GetRequiredService<IServiceTokenSource>().GetTokenAsync(cancellationToken);
    }
}

internal sealed class WarmUpState
{
    private volatile bool _done;

    public bool Done
    {
        get => _done;
        set => _done = value;
    }
}

internal sealed partial class WarmUpService(
    WarmUpState state, Func<IServiceProvider, CancellationToken, Task> warmUp, IServiceProvider services, ILogger<WarmUpService> logger,
    TimeSpan firstRetryPause) : BackgroundService
{
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pause = firstRetryPause;
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await warmUp(services, stoppingToken);
                state.Done = true;
                LogFinished(attempt);
                return;
            }
            // Only the host's own stop is a shutdown. A timeout is a TaskCanceledException (an OperationCanceledException) too, and
            // Duende's token request rethrows it, so a Keycloak that does not answer must be retried, not stop the host.
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(e, attempt, pause);
            }

            try
            {
                await Task.Delay(pause, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, MaxPause.Ticks));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Warm-up finished after {Attempt} attempt(s).")]
    private partial void LogFinished(int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Warm-up attempt {Attempt} failed; trying again in {Pause}.")]
    private partial void LogFailed(Exception exception, int attempt, TimeSpan pause);
}
