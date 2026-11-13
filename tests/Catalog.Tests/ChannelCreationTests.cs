using System.Net.Http.Json;

using NewStore.Catalog.Channels;

namespace NewStore.Catalog.Tests;

public class ChannelCreationTests(SeededCatalogAppFactory factory) : IClassFixture<SeededCatalogAppFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ClientFor(Guid userId) =>
        factory.CreateClient().WithBearer(TestTokens.User(userId, "catalog"));

    private static CreateChannelRequest Request(string slug) => new("Pottery Hour", slug, "Wheel-thrown, mostly.");

    [Fact]
    public async Task Signed_in_user_opens_a_channel()
    {
        var userId = Guid.NewGuid();
        using var client = ClientFor(userId);

        var response = await client.PostAsJsonAsync("/channels", Request("pottery-hour"), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/channels/pottery-hour", response.Headers.Location?.OriginalString);
        var channel = (await response.Content.ReadFromJsonAsync<ChannelResponse>(Ct))!;
        Assert.Equal(("Pottery Hour", "pottery-hour", "Wheel-thrown, mostly."), (channel.Name, channel.Slug, channel.Description));

        // The page the Location header points at exists.
        using var anonymous = factory.CreateClient();
        var page = await anonymous.GetFromJsonAsync<ChannelPage>("/channels/pottery-hour", Ct);
        Assert.Equal(channel.Id, page!.Id);
    }

    [Fact]
    public async Task Second_channel_for_the_same_user_is_409_channel_exists()
    {
        using var client = ClientFor(Guid.NewGuid());
        (await client.PostAsJsonAsync("/channels", Request("first-channel"), Ct)).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/channels", Request("second-channel"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:channel-exists", (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Type);
    }

    [Fact]
    public async Task Taken_slug_is_409()
    {
        using var client = ClientFor(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/channels", Request("orbital-kitchen"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:slug-taken", (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Type);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Upper-Case")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("abc\n")]
    public async Task Invalid_slug_is_400(string slug)
    {
        using var client = ClientFor(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/channels", Request(slug), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Empty_name_is_400()
    {
        using var client = ClientFor(Guid.NewGuid());

        var response = await client.PostAsJsonAsync("/channels", new CreateChannelRequest("", "valid-slug", null), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_open_a_channel()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/channels", Request("anon-channel"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record Problem(string? Type);
}
