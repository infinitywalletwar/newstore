using System.Diagnostics;
using System.Net.Http.Json;

using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Tests;

public class CachingTests(PostgresServer postgres, KafkaServer kafka, RedisCatalogAppFactory factory) : IClassFixture<RedisCatalogAppFactory>
{
    private static async Task<Guid> SomeVideoIdAsync(HttpClient client, string q) =>
        (await client.GetFromJsonAsync<PagedResult<VideoSummary>>($"/videos?q={q}", TestContext.Current.CancellationToken))!
            .Items[0].Id;

    [Fact]
    public async Task Second_read_of_a_video_is_served_from_the_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        var id = await SomeVideoIdAsync(client, "sourdough");
        var path = $"/videos/{id}";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, ct)).StatusCode);
        await WaitForServerSpansAsync(path, 2);

        var servers = factory.ExportedSpans.Snapshot()
            .Where(s => s.Kind == ActivityKind.Server && s.GetTagItem("url.path") as string == path)
            .OrderBy(s => s.StartTimeUtc).ToList();
        var all = factory.ExportedSpans.Snapshot();
        var first = servers[0];
        var second = servers[1];

        Assert.Contains(all, s => s.Source.Name == "Npgsql" && s.ParentSpanId == first.SpanId);
        Assert.DoesNotContain(all, s => s.Source.Name == "Npgsql" && s.ParentSpanId == second.SpanId);
    }

    [Fact]
    public async Task Reads_still_work_when_redis_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        // Own app and container: stopping Redis must not touch the other tests' fixture.
        var own = new RedisCatalogAppFactory(postgres, kafka);
        await own.InitializeAsync();
        await using var _ = own;
        using var client = own.CreateClient();
        var ids = (await client.GetFromJsonAsync<PagedResult<VideoSummary>>("/videos?pageSize=2", ct))!.Items;

        await own.Redis.StopAsync(ct);

        var watch = Stopwatch.StartNew();
        var first = await client.GetAsync($"/videos/{ids[0].Id}", ct);
        var firstTime = watch.Elapsed;
        // The failure opened the 30 s window, so the next uncached read does not wait on Redis again.
        watch.Restart();
        var second = await client.GetAsync($"/videos/{ids[1].Id}", ct);
        var secondTime = watch.Elapsed;
        var ready = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(firstTime < TimeSpan.FromSeconds(2), $"The first read took {firstTime.TotalMilliseconds} ms.");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(secondTime < TimeSpan.FromMilliseconds(500), $"The second read took {secondTime.TotalMilliseconds} ms.");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", await ready.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Ready_is_healthy_with_redis_up()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(ct));
    }

    private async Task WaitForServerSpansAsync(string path, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (factory.ExportedSpans.Snapshot().Count(s => s.Kind == ActivityKind.Server && s.GetTagItem("url.path") as string == path) >= count)
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Fewer than {count} server spans for {path}.");
    }
}
