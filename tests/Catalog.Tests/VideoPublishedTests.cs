using System.Net.Http.Json;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Catalog.Channels;
using NewStore.Catalog.Videos;
using NewStore.Contracts.Media;
using NewStore.Messaging.Kafka;

namespace NewStore.Catalog.Tests;

public class VideoPublishedTests(SeededCatalogAppFactory factory, KafkaServer kafka) : IClassFixture<SeededCatalogAppFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VideoPublished Event(Guid videoId, string playlist = "hls/v/u/master.m3u8", DateTimeOffset? at = null, int seconds = 95) =>
        new()
        {
            EventId = Guid.NewGuid(),
            OccurredAt = at ?? DateTimeOffset.UtcNow,
            VideoId = videoId,
            UploadId = Guid.NewGuid(),
            PlaylistPath = playlist,
            ThumbnailPath = playlist.Replace("master.m3u8", "thumbnail.jpg", StringComparison.Ordinal),
            DurationSeconds = seconds,
        };

    private Task PublishAsync(VideoPublished e) => kafka.PublishAsync(VideoPublished.Topic, e.VideoId.ToString(), e, Ct);

    /// <summary>A new creator with a channel and one draft video.</summary>
    private async Task<(Guid VideoId, string Slug)> DraftAsync()
    {
        var client = factory.CreateClient().WithBearer(TestTokens.User(Guid.NewGuid(), "catalog"));
        var slug = $"p-{Guid.NewGuid():N}"[..20];
        (await client.PostAsJsonAsync("/channels", new CreateChannelRequest("Publisher", slug, null), Ct)).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/videos", new CreateVideoRequest("A draft", null), Ct);
        return ((await response.Content.ReadFromJsonAsync<MyVideo>(Ct))!.Id, slug);
    }

    private async Task<VideoDetails> WaitForPublishedAsync(Guid videoId)
    {
        using var client = factory.CreateClient();
        VideoDetails? details = null;
        await PollUntilAsync(async () =>
        {
            var response = await client.GetAsync($"/videos/{videoId}", Ct);
            details = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<VideoDetails>(Ct) : null;
            return details is not null;
        });
        return details!;
    }

    private async Task<long> InboxCountAsync(Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM inbox_messages WHERE message_id = {messageId}")
            .SingleAsync(Ct);
    }

    private async Task<DateTimeOffset?> PublishedAtAsync(Guid videoId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Videos
            .AsNoTracking().SingleAsync(v => v.Id == videoId, Ct)).PublishedAt;
    }

    [Fact]
    public async Task VideoPublished_publishes_the_draft_with_its_urls()
    {
        var (videoId, _) = await DraftAsync();
        var published = Event(videoId, "hls/abc/def/master.m3u8", seconds: 95);

        await PublishAsync(published);

        var details = await WaitForPublishedAsync(videoId);
        Assert.Equal($"{KafkaSettings.MediaBaseUrl}/hls/abc/def/master.m3u8", details.PlaybackUrl);
        Assert.Equal($"{KafkaSettings.MediaBaseUrl}/hls/abc/def/thumbnail.jpg", details.ThumbnailUrl);
        Assert.Equal(95, details.DurationSeconds);
        Assert.Equal(published.OccurredAt, details.PublishedAt, TimeSpan.FromMilliseconds(1));
        using var client = factory.CreateClient();
        var list = (await client.GetFromJsonAsync<PagedResult<VideoSummary>>("/videos?pageSize=100", Ct))!;
        Assert.Contains(list.Items, v => v.Id == videoId);
    }

    [Fact]
    public async Task VideoPublished_refreshes_the_cached_channel_page()
    {
        var (videoId, slug) = await DraftAsync();
        using var client = factory.CreateClient();
        // Read both pages while the video is a draft, so each holds a cached "nothing here" answer.
        Assert.Empty((await client.GetFromJsonAsync<ChannelPage>($"/channels/{slug}", Ct))!.Videos.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/videos/{videoId}", Ct)).StatusCode);

        await PublishAsync(Event(videoId));

        // Without the tag removal the channel page would stay empty for up to 60 seconds, past this test's 15.
        await PollUntilAsync(async () =>
            (await client.GetFromJsonAsync<ChannelPage>($"/channels/{slug}", Ct))!.Videos.Items.Any(v => v.Id == videoId));
    }

    [Fact]
    public async Task Duplicate_VideoPublished_changes_nothing()
    {
        var (videoId, _) = await DraftAsync();
        var published = Event(videoId, at: new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));

        await PublishAsync(published);
        await WaitForPublishedAsync(videoId);
        // Same event, same message id: Kafka delivers it twice.
        await PublishAsync(published);
        var marker = Event(videoId, "hls/marker/x/master.m3u8");
        await PublishAsync(marker);
        await PollUntilAsync(async () => await InboxCountAsync(marker.EventId) == 1);

        Assert.Equal(published.OccurredAt, await PublishedAtAsync(videoId));
        Assert.Equal(1, await InboxCountAsync(published.EventId));
    }

    [Fact]
    public async Task Second_VideoPublished_for_a_published_video_changes_nothing()
    {
        var (videoId, _) = await DraftAsync();
        var first = Event(videoId, "hls/first/one/master.m3u8", seconds: 10);
        var later = Event(videoId, "hls/later/two/master.m3u8", at: first.OccurredAt.AddMinutes(5), seconds: 20);

        await PublishAsync(first);
        await WaitForPublishedAsync(videoId);
        await PublishAsync(later);
        await PollUntilAsync(async () => await InboxCountAsync(later.EventId) == 1);

        var details = await WaitForPublishedAsync(videoId);
        Assert.Equal($"{KafkaSettings.MediaBaseUrl}/hls/first/one/master.m3u8", details.PlaybackUrl);
        Assert.Equal(10, details.DurationSeconds);
        Assert.Equal(first.OccurredAt, await PublishedAtAsync(videoId));
    }

    [Fact]
    public async Task VideoPublished_for_an_unknown_video_is_dead_lettered()
    {
        var unknown = Event(Guid.NewGuid());
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"dlt-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(VideoPublished.Topic + ".dlt");

        await PublishAsync(unknown);

        // The handler throws InvalidOperationException; after MaxHandlerAttempts (3) the message goes to the dead-letter topic.
        ConsumeResult<string, string>? dead = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (dead is null && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result?.Message.Key == unknown.VideoId.ToString())
            {
                dead = result;
            }
        }

        Assert.NotNull(dead);
        var error = System.Text.Encoding.UTF8.GetString(dead.Message.Headers.GetLastBytes(MessageHeaders.Error));
        Assert.Contains("InvalidOperationException", error);
        Assert.Equal(0, await InboxCountAsync(unknown.EventId));
    }

    private static async Task PollUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        Assert.Fail("The condition did not hold within 15 seconds.");
    }
}
