using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Catalog.Channels;
using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Tests;

public class VideoDraftTests(SeededCatalogAppFactory factory) : IClassFixture<SeededCatalogAppFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SourdoughId = "00000000-0000-7000-8001-000000000011";

    private HttpClient ClientFor(Guid userId) => factory.CreateClient().WithBearer(TestTokens.User(userId, "catalog"));

    /// <summary>A signed-in user who owns a channel with a unique slug.</summary>
    private async Task<(Guid UserId, HttpClient Client, string Slug)> CreatorAsync()
    {
        var userId = Guid.NewGuid();
        var client = ClientFor(userId);
        var slug = $"c-{Guid.NewGuid():N}"[..20];
        (await client.PostAsJsonAsync("/channels", new CreateChannelRequest("Draft Studio", slug, null), Ct)).EnsureSuccessStatusCode();
        return (userId, client, slug);
    }

    private static async Task<MyVideo> CreateVideoAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/videos", new CreateVideoRequest(title, "About " + title), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MyVideo>(Ct))!;
    }

    [Fact]
    public async Task Creator_creates_a_draft_video()
    {
        var (_, client, _) = await CreatorAsync();

        var response = await client.PostAsJsonAsync("/videos", new CreateVideoRequest("  My first cut ", "Raw footage"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var video = (await response.Content.ReadFromJsonAsync<MyVideo>(Ct))!;
        Assert.Equal($"/videos/{video.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(("My first cut", "Draft", null, null), (video.Title, video.Status, video.PublishedAt, video.ThumbnailUrl));
        Assert.True(video.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Draft_is_not_listed_searched_or_found()
    {
        var (_, client, slug) = await CreatorAsync();
        var title = $"Hidden{Guid.NewGuid():N}";
        var draft = await CreateVideoAsync(client, title);
        using var anonymous = factory.CreateClient();

        var list = (await anonymous.GetFromJsonAsync<PagedResult<VideoSummary>>("/videos?pageSize=100", Ct))!;
        var search = (await anonymous.GetFromJsonAsync<PagedResult<VideoSummary>>($"/videos?q={title}", Ct))!;
        var channel = (await anonymous.GetFromJsonAsync<ChannelPage>($"/channels/{slug}", Ct))!;

        Assert.DoesNotContain(list.Items, v => v.Id == draft.Id);
        Assert.Empty(search.Items);
        Assert.Empty(channel.Videos.Items);
        Assert.Equal(0, channel.Videos.Total);
        // A draft is a 404 to everyone, its creator included.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/videos/{draft.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/videos/{draft.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Creating_a_video_without_a_channel_is_409()
    {
        using var client = ClientFor(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/videos", new CreateVideoRequest("Orphan", null), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:no-channel", (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Invalid_title_is_400(string? title)
    {
        var (_, client, _) = await CreatorAsync();

        var response = await client.PostAsJsonAsync("/videos", new CreateVideoRequest(title!, null), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Too_long_title_or_description_is_400()
    {
        var (_, client, _) = await CreatorAsync();

        var longTitle = await client.PostAsJsonAsync("/videos", new CreateVideoRequest(new string('t', 121), null), Ct);
        var longDescription = await client.PostAsJsonAsync("/videos", new CreateVideoRequest("Fine", new string('d', 5001)), Ct);
        var limits = await client.PostAsJsonAsync("/videos", new CreateVideoRequest(new string('t', 120), new string('d', 5000)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, longTitle.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longDescription.StatusCode);
        Assert.Equal(HttpStatusCode.Created, limits.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_create_a_video()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/videos", new CreateVideoRequest("Nope", null), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task My_videos_lists_drafts_and_published_newest_first()
    {
        var (userId, client, _) = await CreatorAsync();
        var first = await CreateVideoAsync(client, "First");
        var second = await CreateVideoAsync(client, "Second");
        // Publish the first one the way the handler does, so the list holds both statuses.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE videos SET status = 'Published', published_at = now(), thumbnail_path = 'hls/x/thumbnail.jpg' WHERE id = {first.Id}", Ct);
        }

        var mine = (await client.GetFromJsonAsync<MyVideo[]>("/me/videos", Ct))!;

        Assert.Equal([second.Id, first.Id], mine.Select(v => v.Id));
        Assert.Equal(["Draft", "Published"], mine.Select(v => v.Status));
        Assert.Null(mine[0].ThumbnailUrl);
        Assert.Equal($"{KafkaSettings.MediaBaseUrl}/hls/x/thumbnail.jpg", mine[1].ThumbnailUrl);
        Assert.NotNull(mine[1].PublishedAt);

        // Another user's list does not include them.
        using var other = ClientFor(Guid.NewGuid());
        Assert.Empty((await other.GetFromJsonAsync<MyVideo[]>("/me/videos", Ct))!);
        Assert.NotEqual(Guid.Empty, userId);
    }

    [Fact]
    public async Task Owner_lookup_accepts_the_media_client_and_returns_the_status()
    {
        var (userId, client, _) = await CreatorAsync();
        var draft = await CreateVideoAsync(client, "Owned");
        using var media = factory.CreateClient().WithBearer(TestTokens.Client("media", "catalog"));

        var owner = await media.GetFromJsonAsync<VideoOwner>($"/videos/{draft.Id}/owner", Ct);

        Assert.Equal(new VideoOwner(draft.Id, userId, "Draft"), owner);
    }

    [Fact]
    public async Task Owner_lookup_refuses_other_clients()
    {
        using var orders = factory.CreateClient().WithBearer(TestTokens.Client("orders", "catalog"));
        using var user = factory.CreateClient().WithBearer(TestTokens.User(Guid.NewGuid(), "catalog"));

        Assert.Equal(HttpStatusCode.Forbidden, (await orders.GetAsync($"/videos/{SourdoughId}/owner", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/videos/{SourdoughId}/owner", Ct)).StatusCode);
    }

    [Fact]
    public async Task Sample_videos_play_the_sample()
    {
        using var client = factory.CreateClient();

        var video = (await client.GetFromJsonAsync<VideoDetails>($"/videos/{SourdoughId}", Ct))!;

        Assert.Equal("http://media.test/newstore-media/hls/sample/master.m3u8", video.PlaybackUrl);
        Assert.Equal("http://media.test/newstore-media/hls/sample/thumbnail.jpg", video.ThumbnailUrl);
        var summary = (await client.GetFromJsonAsync<PagedResult<VideoSummary>>("/videos?q=sourdough", Ct))!.Items[0];
        Assert.Equal(video.ThumbnailUrl, summary.ThumbnailUrl);
    }

    private sealed record Problem(string? Type);
}
