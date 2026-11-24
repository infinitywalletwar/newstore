using System.Diagnostics;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace NewStore.TestSupport;

/// <summary>
/// Starts <typeparamref name="TProgram"/> in memory. Each entry in <see cref="Settings"/> overrides configuration.
/// Spans are isolated per factory: every client request carries a trace id that starts with
/// <see cref="TraceIdPrefix"/>, and <see cref="ExportedSpans"/> keeps only spans with that prefix.
/// </summary>
public class NewStoreAppFactory<TProgram> : WebApplicationFactory<TProgram>
    where TProgram : class
{
    private static readonly TimeSpan DefaultSpanTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Overrides configuration. It starts with test values for the token settings: <c>Auth:Issuer</c> is the test issuer and
    /// <c>Auth:UserClients:0</c> is <c>gateway</c>. <c>Auth:Audience</c> is not set here: each service names itself in its own
    /// <c>appsettings.json</c>, so a test with the wrong audience fails as it would in production.
    /// </summary>
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Auth:Issuer"] = TestTokens.Issuer,
        ["Auth:UserClients:0"] = "gateway",
    };

    /// <summary>8 random hex characters; 4 random bytes are enough to keep two factories in one process apart.</summary>
    public string TraceIdPrefix { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>The spans of this app's requests (and their children). Other tests share the app, so don't assume it starts empty.</summary>
    public SpanCollection ExportedSpans { get; }

    private readonly List<Func<IServiceProvider, CancellationToken, Task>> _beforeStart = [];

    public NewStoreAppFactory()
    {
        ExportedSpans = new SpanCollection(TraceIdPrefix);
    }

    /// <summary>Server spans end just after the response reaches the client, so poll for the span.</summary>
    public async Task<Activity> WaitForSpanAsync(Func<Activity, bool> match, TimeSpan? timeout = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + (timeout ?? DefaultSpanTimeout);
        while (DateTime.UtcNow < deadline)
        {
            var span = ExportedSpans.Snapshot().FirstOrDefault(match);
            if (span is not null)
            {
                return span;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        Assert.Fail($"No matching span was exported within {timeout ?? DefaultSpanTimeout}.");
        return null!;
    }

    /// <summary>
    /// Makes the app apply <typeparamref name="TContext"/>'s migrations when it starts, before any hosted service (the outbox relay,
    /// workers, consumers) starts, as the one-shot <c>*-migrate</c> job does in Compose. Call it before the app first starts.
    /// </summary>
    public void MigrateBeforeStart<TContext>()
        where TContext : DbContext => _beforeStart.Add(async (services, ct) =>
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync(ct);
        });

    /// <summary>
    /// Applies <typeparamref name="TContext"/>'s migrations now. It resolves <see cref="WebApplicationFactory{TEntryPoint}.Services"/>,
    /// which starts the app first, so use <see cref="MigrateBeforeStart{TContext}"/> unless a test migrates on purpose after the start.
    /// </summary>
    public async Task MigrateAsync<TContext>()
        where TContext : DbContext
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

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
        {
            // First in the list, so no other hosted service's StartingAsync runs before it. The host calls every StartingAsync
            // before any StartAsync, so the app's own hosted services (registered as plain IHostedService) start after it.
            services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(sp => new BeforeStartService(sp, _beforeStart)));
            services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(ExportedSpans));

            // Tokens are checked against the test key, and calls to other services carry a test token.
            // Both are no-ops for a service that has not called AddNewStoreAuth or AddServiceClient.
            services.UseTestSigningKey();
            services.UseTestServiceTokens();
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        // W3C trace context: version-traceid-parentid-flags. The server span joins this trace, so its
        // trace id starts with our prefix. Flag 01 = sampled.
        var traceId = TraceIdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var parentId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{parentId}-01");
    }

    /// <summary>Runs the before-start steps in <c>StartingAsync</c>, the first stage of the host's start.</summary>
    private sealed class BeforeStartService(IServiceProvider services, List<Func<IServiceProvider, CancellationToken, Task>> steps)
        : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            foreach (var step in steps)
            {
                await step(services, cancellationToken);
            }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
