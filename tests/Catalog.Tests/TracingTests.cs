using System.Diagnostics;

namespace NewStore.Catalog.Tests;

public class TracingTests(PostgresCatalogAppFactory factory) : IClassFixture<PostgresCatalogAppFactory>
{
    [Fact]
    public async Task Request_to_unknown_path_is_traced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/no-such-page", ct);
        var span = await factory.WaitForSpanAsync(s => s.GetTagItem("url.path") as string == "/no-such-page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal(404, span.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task Health_requests_are_not_traced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", ct)).StatusCode);
        // Once this span arrives, the health requests before it have finished too.
        await client.GetAsync("/after-health", ct);
        await factory.WaitForSpanAsync(s => s.GetTagItem("url.path") as string == "/after-health");

        var spans = factory.ExportedSpans.Snapshot();
        Assert.DoesNotContain(spans, s => (s.GetTagItem("url.path") as string)?.StartsWith("/health", StringComparison.Ordinal) == true);
        // The readiness query is a child of the dropped request, so it is dropped as well. Spans are
        // isolated per factory, so any Npgsql query span here belongs to this app.
        Assert.DoesNotContain(spans, s => s.Source.Name == "Npgsql" && s.GetTagItem("db.query.text") as string == "SELECT 1");
    }

    [Fact]
    public async Task Activity_source_named_NewStore_dot_something_is_collected()
    {
        using var client = factory.CreateClient();
        using var source = new ActivitySource("NewStore.Probe");
        // Join a trace that carries this factory's prefix, as a request would.
        var traceId = ActivityTraceId.CreateFromString(factory.TraceIdPrefix + new string('a', 24));
        var parent = new ActivityContext(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

        using (source.StartActivity("probe", ActivityKind.Internal, parent))
        {
        }

        var span = await factory.WaitForSpanAsync(s => s.Source.Name == "NewStore.Probe");
        Assert.Equal("probe", span.OperationName);
    }
}
