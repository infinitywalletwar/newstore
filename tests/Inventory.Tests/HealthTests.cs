namespace NewStore.Inventory.Tests;

public class HealthTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>
{
    [Fact]
    public async Task Ready_is_healthy_with_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = fixture.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
