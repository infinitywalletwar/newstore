using System.Net.Http.Json;

using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Tests;

public class VideoOwnerTests(SeededCatalogAppFactory factory) : IClassFixture<SeededCatalogAppFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SourdoughId = "00000000-0000-7000-8001-000000000011";

    [Fact]
    public async Task Sample_videos_have_fixed_ids()
    {
        using var client = factory.CreateClient();

        for (var creator = 1; creator <= 6; creator++)
        {
            for (var position = 1; position <= 8; position++)
            {
                var id = Guid.Parse($"00000000-0000-7000-8001-0000000000{creator}{position}");
                var response = await client.GetAsync($"/videos/{id}", Ct);
                Assert.True(response.IsSuccessStatusCode, $"video {id} is missing");
            }
        }

        var sourdough = await client.GetFromJsonAsync<VideoDetails>($"/videos/{SourdoughId}", Ct);
        Assert.Equal("Sourdough in Zero Gravity", sourdough!.Title);
    }

    [Fact]
    public async Task Owner_lookup_returns_the_channels_creator()
    {
        using var client = factory.CreateClient().WithBearer(TestTokens.Client("inventory", "catalog"));

        var owner = await client.GetFromJsonAsync<VideoOwner>($"/videos/{SourdoughId}/owner", Ct);

        Assert.Equal(new VideoOwner(Guid.Parse(SourdoughId), Guid.Parse("00000000-0000-7000-8000-000000000001")), owner);
    }

    [Fact]
    public async Task Owner_lookup_needs_the_inventory_client()
    {
        using var user = factory.CreateClient().WithBearer(TestTokens.User(Guid.NewGuid(), "catalog"));
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/videos/{SourdoughId}/owner", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/videos/{SourdoughId}/owner", Ct)).StatusCode);
    }

    [Fact]
    public async Task Owner_of_an_unknown_video_is_404()
    {
        using var client = factory.CreateClient().WithBearer(TestTokens.Client("inventory", "catalog"));

        var response = await client.GetAsync($"/videos/{Guid.NewGuid()}/owner", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
