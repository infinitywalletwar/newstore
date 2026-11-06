using System.Net.Http.Json;

using NewStore.Catalog.Channels;

namespace NewStore.Catalog.Tests;

public class ChannelTests(SeededCatalogAppFactory factory) : IClassFixture<SeededCatalogAppFactory>
{
    [Fact]
    public async Task Channel_page_lists_its_videos_newest_first()
    {
        using var client = factory.CreateClient();

        var page = await client.GetFromJsonAsync<ChannelPage>("/channels/orbital-kitchen", TestContext.Current.CancellationToken);

        Assert.NotNull(page);
        Assert.Equal("Orbital Kitchen", page.Name);
        Assert.Equal(8, page.Videos.Total);
        Assert.Equal(8, page.Videos.Items.Count);
        Assert.Equal(page.Videos.Items.OrderByDescending(v => v.PublishedAt), page.Videos.Items);
        Assert.All(page.Videos.Items, v => Assert.Equal(page.Id, v.Channel.Id));
    }

    [Fact]
    public async Task Unknown_channel_returns_404_problem()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/channels/no-such-channel", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
