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

        var response = await client.PostAsJsonAsync("/editions", new
        {
            videoId = Guid.NewGuid(),
            creatorId = Guid.NewGuid(),
            format = "Dvd",
            price = 19.5m,
            copies = 10,
        }, ct);

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

        var response = await client.PostAsJsonAsync("/editions", new
        {
            videoId = Guid.NewGuid(),
            creatorId = Guid.NewGuid(),
            format = "Digital",
            price = 5m,
            copies,
        }, ct);

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
}
