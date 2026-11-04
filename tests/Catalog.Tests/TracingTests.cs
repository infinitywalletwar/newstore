using System.Diagnostics;

namespace NewStore.Catalog.Tests;

public class TracingTests(PostgresCatalogAppFactory factory)
{
    [Fact]
    public async Task Request_to_unknown_path_is_traced()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/no-such-page", ct);
        var span = await WaitForSpanAsync("/no-such-page", ct);

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
        await WaitForSpanAsync("/after-health", ct);

        var spans = factory.ExportedSpans.Snapshot();
        Assert.DoesNotContain(spans, s => (s.GetTagItem("url.path") as string)?.StartsWith("/health") == true);
        // The readiness query is a child of the dropped request, so it is dropped as well.
        Assert.DoesNotContain(spans, s => s.Source.Name == "Npgsql");
    }

    /// <summary>Server spans end just after the response reaches the client, so poll for the span.</summary>
    private async Task<Activity> WaitForSpanAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var span = factory.ExportedSpans.Snapshot()
                .FirstOrDefault(s => s.GetTagItem("url.path") as string == path);
            if (span is not null)
            {
                return span;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }

        Assert.Fail($"No span with url.path '{path}' was exported within 5 seconds.");
        return null!;
    }
}