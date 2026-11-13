using System.Net.Http.Json;
using System.Text.Json;

namespace NewStore.Inventory.Tests;

public class EditionTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>
{
    [Fact]
    public async Task Create_edition_returns_201_with_all_copies_left()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();

        var creator = Guid.NewGuid();
        var video = Guid.NewGuid();
        FakeCatalog.SetOwner(video, creator);

        var response = await client.SendAsUserAsync(creator, HttpMethod.Post, "/editions", new
        {
            videoId = video,
            format = "Dvd",
            price = 19.5m,
            copies = 10,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<EditionDto>(ct))!;
        Assert.Equal(10, created.CopiesLeft);
        Assert.Equal(10, created.CopiesTotal);
        Assert.Equal(0, created.CopiesSold);
        Assert.Equal("Dvd", created.Format);
        Assert.Equal(19.5m, created.Price);

        var location = response.Headers.Location;
        Assert.Equal($"/editions/{created.Id}", location?.OriginalString);
        var fetched = await client.GetFromJsonAsync<EditionDto>(location, ct);
        Assert.Equal(created, fetched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public async Task Create_edition_with_copies_outside_the_range_returns_400(int copies)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();

        var creator = Guid.NewGuid();
        var video = Guid.NewGuid();
        FakeCatalog.SetOwner(video, creator);

        var response = await client.SendAsUserAsync(creator, HttpMethod.Post, "/editions", new
        {
            videoId = video,
            format = "Digital",
            price = 5m,
            copies,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("copies", out _));
    }

    [Fact]
    public async Task Unknown_edition_returns_404_problem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();

        var response = await client.GetAsync($"/editions/{Guid.NewGuid()}", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:newstore:problem:not-found", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Copies_of_a_new_edition_is_an_empty_list()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();

        var copies = await client.GetCopiesAsync(edition.Id);

        Assert.Empty(copies);
    }

    [Fact]
    public async Task Edit_before_any_reservation_changes_price_and_copies()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);

        var response = await client.UpdateEditionAsync(edition.Id, price: 12.50m, copies: 5);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<EditionDto>(ct))!;
        Assert.Equal(12.50m, updated.Price);
        Assert.Equal(5, updated.CopiesTotal);
        Assert.Equal(5, updated.CopiesLeft);
        Assert.Equal(updated, await client.GetEditionAsync(edition.Id));
    }

    [Fact]
    public async Task Edit_with_no_fields_returns_400_validation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();

        var response = await client.SendAsUserAsync(edition.CreatorId, HttpMethod.Patch, $"/editions/{edition.Id}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("request", out _));
    }

    [Fact]
    public async Task Edit_of_unknown_edition_returns_404()
    {
        using var client = fixture.CreateClient();

        var response = await client.UpdateEditionAsync(Guid.NewGuid(), copies: 5);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("urn:newstore:problem:not-found", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task End_sale_withdraws_unsold_copies_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);

        var first = await client.EndSaleAsync(edition.Id);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var ended = (await first.Content.ReadFromJsonAsync<EditionDto>(ct))!;
        Assert.Equal(0, ended.CopiesLeft);
        Assert.NotNull(ended.EndedAt);

        var second = await client.EndSaleAsync(edition.Id);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(ended.EndedAt, (await second.Content.ReadFromJsonAsync<EditionDto>(ct))!.EndedAt);

        var edit = await client.UpdateEditionAsync(edition.Id, copies: 5);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("urn:newstore:problem:sale-ended", (await edit.ReadProblemAsync()).Type);
    }
}
