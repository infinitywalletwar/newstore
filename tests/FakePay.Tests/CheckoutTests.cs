using System.Security.Cryptography;
using System.Text;

namespace NewStore.FakePay.Tests;

public class CheckoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pay_redirects_to_the_success_url_and_sends_a_signed_completed_webhook()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client, successUrl: "http://shop.test/done?o=1");

        var page = await client.GetStringAsync($"/checkout/{session.Id}", Ct);
        Assert.Contains("data-testid=\"fakepay-pay\"", page, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"fakepay-decline\"", page, StringComparison.Ordinal);
        Assert.Contains("12.50", page, StringComparison.Ordinal);

        var pay = await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);

        Assert.Equal(HttpStatusCode.SeeOther, pay.StatusCode);
        Assert.Equal("http://shop.test/done?o=1", pay.Headers.Location?.ToString());

        var webhook = Assert.Single(await app.WaitForWebhooksAsync(1));
        Assert.StartsWith("evt_", webhook.Id, StringComparison.Ordinal);
        Assert.Equal("checkout.session.completed", webhook.Type);
        Assert.Equal(session.Id, webhook.SessionId);
        AssertSigned(webhook, app.Clock.GetUtcNow());
    }

    /// <summary>Checks the header against the receiver's rule, computed here from the documented layout and not with FakePay's signer.</summary>
    private static void AssertSigned(ReceivedWebhook webhook, DateTimeOffset now)
    {
        var parts = webhook.Signature!.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(2, parts.Count);
        Assert.Equal(now.ToUnixTimeSeconds(), long.Parse(parts["t"], System.Globalization.CultureInfo.InvariantCulture));

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(FakePayApp.WebhookSecret),
            Encoding.UTF8.GetBytes($"{parts["t"]}.{webhook.Body}")));
        Assert.Equal(expected, parts["v1"]);
    }

    [Fact]
    public async Task Decline_shows_an_error_and_keeps_the_session_open()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);

        var decline = await client.PostAsync($"/checkout/{session.Id}/decline", content: null, Ct);
        var body = await decline.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        Assert.Contains("data-testid=\"fakepay-error\"", body, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"fakepay-pay\"", body, StringComparison.Ordinal);

        // Still open: the page loads and a later pay succeeds. Nothing was sent to the receiver in between.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/checkout/{session.Id}", Ct)).StatusCode);
        Assert.Empty(app.Webhooks());
        Assert.Equal(HttpStatusCode.SeeOther, (await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Paying_a_completed_session_again_is_409()
    {
        await using var app = new FakePayApp();
        using var client = app.CreateApiClient();
        var session = await app.CreateSessionAsync(client);
        await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);

        var again = await client.PostAsync($"/checkout/{session.Id}/pay", content: null, Ct);
        var page = await client.GetAsync($"/checkout/{session.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, page.StatusCode);
        // One completed event, not two.
        await Task.Delay(200, Ct);
        Assert.Single(app.Webhooks());
    }

    [Fact]
    public async Task Test_pay_endpoint_is_404_unless_enabled()
    {
        await using var off = new FakePayApp();
        using var offClient = off.CreateApiClient();
        var offSession = await off.CreateSessionAsync(offClient);

        await using var on = new FakePayApp(enableTestEndpoints: true);
        using var onClient = on.CreateApiClient();
        var onSession = await on.CreateSessionAsync(onClient);

        Assert.Equal(HttpStatusCode.NotFound, (await offClient.PostAsync($"/test/sessions/{offSession.Id}/pay", content: null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await onClient.PostAsync($"/test/sessions/{onSession.Id}/pay", content: null, Ct)).StatusCode);
        Assert.Equal("checkout.session.completed", Assert.Single(await on.WaitForWebhooksAsync(1)).Type);
    }
}
