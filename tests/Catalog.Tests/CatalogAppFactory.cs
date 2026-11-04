using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace NewStore.Catalog.Tests;

/// <summary>Starts Catalog in memory. Each entry in <see cref="Settings"/> overrides configuration.</summary>
public class CatalogAppFactory : WebApplicationFactory<Program>
{
    public Dictionary<string, string?> Settings { get; } = [];

    /// <summary>Every span the app exports. Other tests share the app, so don't assume it starts empty.</summary>
    public SpanSink ExportedSpans { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: tests then ignore appsettings.Development.json, which is meant for `dotnet run`.
        builder.UseEnvironment("Testing");

        // Outside Development the host skips scope and service-graph validation. Turn both back on.
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
            services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(ExportedSpans)));
    }
}