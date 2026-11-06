using System.Text.Json;

namespace NewStore.Catalog.Tests;

public class ProblemDetailsTests(PostgresCatalogAppFactory factory) : IClassFixture<PostgresCatalogAppFactory>
{
    [Fact]
    public async Task Unknown_path_returns_a_404_problem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/no-such-page", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(404, body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(body.RootElement.TryGetProperty("openapi", out _));
        Assert.True(body.RootElement.TryGetProperty("paths", out _));
    }
}
