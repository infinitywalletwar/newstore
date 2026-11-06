using System.Net.Http.Json;


using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Tests;

public class VideoTests(SeededCatalogAppFactory factory) : IClassFixture<SeededCatalogAppFactory>
{
    private static Task<PagedResult<VideoSummary>?> ListAsync(HttpClient client, string query) =>
        client.GetFromJsonAsync<PagedResult<VideoSummary>>($"/videos{query}", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Browse_returns_videos_newest_first_with_paging()
    {
        using var client = factory.CreateClient();

        var first = await ListAsync(client, "");
        var third = await ListAsync(client, "?page=3");

        Assert.NotNull(first);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(48, first.Total);
        Assert.Equal(first.Items.OrderByDescending(v => v.PublishedAt), first.Items);
        Assert.NotNull(third);
        Assert.Equal(8, third.Items.Count);
        // Page 3 is older than page 1.
        Assert.True(third.Items[0].PublishedAt < first.Items[^1].PublishedAt);
    }

    [Fact]
    public async Task PageSize_above_100_returns_400()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/videos?pageSize=101", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Page_zero_returns_400()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/videos?page=0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_by_title_finds_the_video()
    {
        using var client = factory.CreateClient();

        var result = await ListAsync(client, "?q=sourdough");

        Assert.NotNull(result);
        Assert.Contains(result.Items, v => v.Title == "Sourdough in Zero Gravity");
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task Search_by_channel_name_finds_its_videos()
    {
        using var client = factory.CreateClient();

        var result = await ListAsync(client, "?q=orbital");

        Assert.NotNull(result);
        Assert.Equal(8, result.Total);
        Assert.All(result.Items, v => Assert.Equal("Orbital Kitchen", v.Channel.Name));
    }

    [Fact]
    public async Task Search_with_no_match_returns_an_empty_page()
    {
        using var client = factory.CreateClient();

        var result = await ListAsync(client, "?q=xyzzy");

        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task Video_details_include_the_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        var summary = (await ListAsync(client, "?q=sourdough"))!.Items.Single();

        var details = await client.GetFromJsonAsync<VideoDetails>($"/videos/{summary.Id}", ct);

        Assert.NotNull(details);
        Assert.Equal("Sourdough in Zero Gravity", details.Title);
        Assert.False(string.IsNullOrWhiteSpace(details.Description));
        Assert.Equal("orbital-kitchen", details.Channel.Slug);
    }

    [Fact]
    public async Task Unknown_video_returns_404_problem()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/videos/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
