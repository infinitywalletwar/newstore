using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using NewStore.ServiceDefaults.HealthChecks;
using NewStore.ServiceDefaults.Telemetry;

namespace NewStore.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Registers what every service shares: the "self" liveness check, telemetry, RFC 9457 problem details
    /// for error responses and the OpenAPI document.
    /// </summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // Liveness answers "is the process running?", so it checks nothing external.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [HealthCheckExtensions.LiveTag]);

        builder.AddDefaultTelemetry();

        // Kestrel's listen backlog, the queue of connections the kernel has accepted but the app has not, is 512 by default
        // (SocketTransportOptions.Backlog: "Defaults to 512 pending connections",
        // https://github.com/dotnet/aspnetcore/blob/v10.0.14/src/Servers/Kestrel/Transport.Sockets/src/SocketTransportOptions.cs).
        // When a burst fills it, the load test measured 78 to 483 `ListenOverflows` per run at Orders, and a client whose SYN is
        // lost waits for its retransmission: RFC 6298 section 2.1 says "the sender SHOULD set RTO <- 1 second" until an RTT is
        // measured (https://www.rfc-editor.org/rfc/rfc6298). listen(2) says a backlog above /proc/sys/net/core/somaxconn "is silently
        // capped to that value" and "Since Linux 5.4, the default in this file is 4096; in earlier kernels, the default value is 128"
        // (https://man7.org/linux/man-pages/man2/listen.2.html). 4096 is therefore the most that has an effect on a kernel with the default.
        builder.Services.Configure<SocketTransportOptions>(options => options.Backlog = 4096);

        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();

        return builder;
    }

    /// <summary>
    /// Services call this before any other middleware. It turns unhandled exceptions and empty 4xx/5xx
    /// responses into application/problem+json, maps /health/live (only checks tagged "live"),
    /// /health/ready (every check) and the OpenAPI document at /openapi/v1.json in every environment.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckExtensions.LiveTag),
        });
        app.MapHealthChecks("/health/ready");
        app.MapOpenApi();

        return app;
    }
}
