using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NewStore.ServiceDefaults.Telemetry;

internal static class TelemetryExtensions
{
    /// <summary>
    /// Traces, metrics and logs for every service. Export goes over OTLP to the collector, and only
    /// when OTEL_EXPORTER_OTLP_ENDPOINT is set. Tracing is always registered, so tests can attach
    /// an in-memory exporter.
    /// </summary>
    public static IHostApplicationBuilder AddDefaultTelemetry(this IHostApplicationBuilder builder)
    {
        // service.name is how Tempo, Prometheus and Loki tell services apart.
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } configured
            ? configured
            : builder.Environment.ApplicationName;

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                // A dropped request starts no activity, so its child spans (the readiness query)
                // are dropped too: the default sampler follows the parent's decision.
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsHealthPath(context.Request.Path))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Connection-pool usage and waits: the readiness check no longer reports a full pool.
                .AddNpgsqlInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>True for /health and anything under it, but not for /healthy or /healthcare.</summary>
    private static bool IsHealthPath(PathString path) => path.StartsWithSegments("/health");
}
