using System.Net;

namespace NewStore.Orders.Tests;

/// <summary>Orders' real warm-up (JwtBearer metadata, the service token, one call to Inventory) against the stubs.</summary>
public class OrdersWarmUpTests(PostgresServer postgres, KafkaServer kafka)
{
    [Fact]
    public async Task Readiness_waits_for_the_warm_up_and_liveness_does_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        await using var app = new OrdersFixture(postgres, kafka) { WarmUp = true };
        // The first call of the warm-up waits here, so the test can look at the app while it is warming up. The route goes in before
        // InitializeAsync: migrating the database starts the app, and the warm-up with it.
        app.Inventory.Handler.On(HttpMethod.Get, "/health/live", _ =>
        {
            release.Wait(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await app.InitializeAsync();
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);

        release.Set();
        await OrdersFixture.WaitAsync(
            async () => (await client.GetAsync("/health/ready", ct)).StatusCode == HttpStatusCode.OK, "readiness after the warm-up");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);
        Assert.Single(app.Inventory.Handler.Requests, r => r.RequestUri!.AbsolutePath == "/health/live");
    }
}
