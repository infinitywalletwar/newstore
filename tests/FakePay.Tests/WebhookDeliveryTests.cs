using Microsoft.Extensions.Logging;

namespace NewStore.FakePay.Tests;

public class WebhookDeliveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Webhook_is_retried_until_it_gets_a_2xx()
    {
        await using var app = new FakePayApp();
        var attempts = 0;
        app.ReceiverReply = _ =>
            new HttpResponseMessage(Interlocked.Increment(ref attempts) <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);

        await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);

        var received = await app.WaitForWebhooksAsync(3);
        // Three attempts: the same event each time. After the 200 it stops.
        Assert.Single(received.Select(r => r.Id).Distinct());
        await Task.Delay(300, Ct);
        Assert.Equal(3, app.Webhooks().Count);
    }

    [Fact]
    public async Task A_refused_webhook_is_logged_as_a_warning_on_every_refusal()
    {
        await using var app = new FakePayApp();
        var attempts = 0;
        app.ReceiverReply = _ =>
            new HttpResponseMessage(Interlocked.Increment(ref attempts) <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);

        await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);
        var received = await app.WaitForWebhooksAsync(3);

        var warnings = app.Logs.Snapshot()
            .Where(l => l.Category.EndsWith("WebhookSender", StringComparison.Ordinal) && l.Level == LogLevel.Warning)
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal(
            $"Webhook {received[0].Id} was not accepted (ServiceUnavailable); it will be sent again.", w.Message));
    }

    [Fact]
    public async Task DeliverTwice_sends_each_webhook_twice()
    {
        await using var app = new FakePayApp(deliverTwice: true);
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);

        await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);

        var received = await app.WaitForWebhooksAsync(2);
        Assert.Equal(received[0].Id, received[1].Id);
        await Task.Delay(300, Ct);
        Assert.Equal(2, app.Webhooks().Count);
    }
}
