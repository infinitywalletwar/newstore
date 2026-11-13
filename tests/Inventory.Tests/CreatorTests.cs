using System.Net.Http.Json;
using System.Text.Json;

namespace NewStore.Inventory.Tests;

/// <summary>Who may create and change editions. Catalog is <see cref="FakeCatalog"/>; Inventory asks it who owns a video.</summary>
public class CreatorTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object> NewEdition(Guid videoId, params (string Key, object Value)[] extra)
    {
        var body = new Dictionary<string, object>
        {
            ["videoId"] = videoId,
            ["format"] = "Digital",
            ["price"] = 4.99m,
            ["copies"] = 5,
        };
        foreach (var (key, value) in extra)
        {
            body[key] = value;
        }

        return body;
    }

    [Fact]
    public async Task Create_edition_takes_the_creator_from_the_token()
    {
        using var client = fixture.CreateClient();
        var creator = Guid.NewGuid();
        var video = Guid.NewGuid();
        FakeCatalog.SetOwner(video, creator);

        // The body names someone else as the creator. Nothing reads it.
        var response = await client.SendAsUserAsync(
            creator, HttpMethod.Post, "/editions", NewEdition(video, ("creatorId", Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(creator, (await response.Content.ReadFromJsonAsync<EditionDto>(Ct))!.CreatorId);
        Assert.Contains(fixture.Catalog.Requests, r => r.RequestUri!.AbsolutePath == $"/videos/{video}/owner");
    }

    [Fact]
    public async Task Create_edition_without_a_token_is_401()
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/editions", NewEdition(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_edition_for_someone_elses_video_is_403()
    {
        using var client = fixture.CreateClient();
        var video = Guid.NewGuid();
        FakeCatalog.SetOwner(video, Guid.NewGuid());

        var response = await client.SendAsUserAsync(Guid.NewGuid(), HttpMethod.Post, "/editions", NewEdition(video));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:newstore:problem:not-your-video", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Create_edition_for_an_unknown_video_is_400()
    {
        using var client = fixture.CreateClient();

        var response = await client.SendAsUserAsync(Guid.NewGuid(), HttpMethod.Post, "/editions", NewEdition(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("videoId", out _));
    }

    [Fact]
    public async Task Create_edition_when_catalog_is_down_is_503()
    {
        using var client = fixture.CreateClient();
        var video = Guid.NewGuid();
        FakeCatalog.Break(video);

        var response = await client.SendAsUserAsync(Guid.NewGuid(), HttpMethod.Post, "/editions", NewEdition(video));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("urn:newstore:problem:dependency-unavailable", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Creator_cannot_change_another_creators_edition()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        var stranger = Guid.NewGuid();

        var patch = await client.UpdateEditionAsync(edition.Id, price: 1m, asUser: stranger);
        var endSale = await client.EndSaleAsync(edition.Id, asUser: stranger);

        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal("urn:newstore:problem:not-your-edition", (await patch.ReadProblemAsync()).Type);
        Assert.Equal(HttpStatusCode.Forbidden, endSale.StatusCode);
        Assert.Equal("urn:newstore:problem:not-your-edition", (await endSale.ReadProblemAsync()).Type);
        Assert.Equal(edition, await client.GetEditionAsync(edition.Id));
    }

    [Fact]
    public async Task Creator_can_change_their_own_edition()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);

        var patch = await client.UpdateEditionAsync(edition.Id, price: 7.5m, asUser: edition.CreatorId);
        var endSale = await client.EndSaleAsync(edition.Id, asUser: edition.CreatorId);

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(7.5m, (await patch.Content.ReadFromJsonAsync<EditionDto>(Ct))!.Price);
        Assert.Equal(HttpStatusCode.OK, endSale.StatusCode);
        Assert.NotNull((await client.GetEditionAsync(edition.Id)).EndedAt);
    }

    [Fact]
    public async Task Copies_list_is_only_for_the_edition_creator()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();

        var own = await client.GetCopiesResponseAsync(edition.Id, edition.CreatorId);
        var other = await client.GetCopiesResponseAsync(edition.Id, Guid.NewGuid());
        var anonymous = await client.GetAsync($"/editions/{edition.Id}/copies", Ct);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("urn:newstore:problem:not-your-edition", (await other.ReadProblemAsync()).Type);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Anyone_can_read_an_edition()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();

        var response = await client.GetAsync($"/editions/{edition.Id}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
