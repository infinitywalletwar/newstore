using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Payments;
using NewStore.Payments.Payments;

namespace NewStore.Payments.Tests;

/// <summary>The webhook, step by step: signature, replay window, duplicate, guarded move, one outbox row.</summary>
[Collection("payments")]
public class WebhookTests(PaymentsFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(PaymentResponse Payment, string SessionId)> NewPaymentAsync()
    {
        var payment = await fixture.CreatePaymentAsync();
        var attempt = await fixture.GetAttemptAsync(payment.Id);
        return (payment, attempt.ProviderSessionId!);
    }

    [Fact]
    public async Task Signed_completed_webhook_publishes_PaymentSucceeded_once()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();

        var response = await TestWebhooks.PostAsync(client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Completed, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PaymentStatus.Succeeded, (await fixture.GetAttemptAsync(payment.Id)).Status);
        Assert.Equal(1, await fixture.CountOutboxAsync<PaymentSucceeded>(payment.OrderId));

        // The row carries the payment, the order and the amount that Orders needs.
        var row = await fixture.QueryAsync(db => db.Set<NewStore.Messaging.Outbox.OutboxMessage>()
            .SingleAsync(m => m.Type == typeof(PaymentSucceeded).FullName && m.Key == payment.OrderId.ToString(), Ct));
        Assert.Equal(PaymentEvents.Topic, row.Topic);
        var published = System.Text.Json.JsonSerializer.Deserialize<PaymentSucceeded>(row.Payload)!;
        Assert.Equal(payment.Id, published.PaymentId);
        Assert.Equal(payment.OrderId, published.OrderId);
        Assert.Equal(payment.Amount, published.Amount);
    }

    [Fact]
    public async Task Duplicate_webhook_publishes_nothing_new()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        var eventId = $"evt_{Guid.NewGuid():N}";

        var first = await TestWebhooks.PostAsync(client, eventId, TestWebhooks.Completed, sessionId);
        var again = await TestWebhooks.PostAsync(client, eventId, TestWebhooks.Completed, sessionId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await fixture.CountOutboxAsync<PaymentSucceeded>(payment.OrderId));
        Assert.Equal(1, await fixture.QueryAsync(db => db.ProviderEvents.CountAsync(e => e.EventId == eventId, Ct)));
    }

    [Fact]
    public async Task Ten_parallel_deliveries_of_one_webhook_publish_once()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        var eventId = $"evt_{Guid.NewGuid():N}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            TestWebhooks.PostAsync(client, eventId, TestWebhooks.Completed, sessionId)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await fixture.CountOutboxAsync<PaymentSucceeded>(payment.OrderId));
    }

    [Fact]
    public async Task Expired_session_fails_the_payment()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();

        await TestWebhooks.PostAsync(client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Expired, sessionId);

        Assert.Equal(PaymentStatus.Failed, (await fixture.GetAttemptAsync(payment.Id)).Status);
        var row = await fixture.QueryAsync(db => db.Set<NewStore.Messaging.Outbox.OutboxMessage>()
            .SingleAsync(m => m.Type == typeof(PaymentFailed).FullName && m.Key == payment.OrderId.ToString(), Ct));
        Assert.Equal("expired", System.Text.Json.JsonSerializer.Deserialize<PaymentFailed>(row.Payload)!.Reason);
    }

    [Fact]
    public async Task Expired_after_completed_changes_nothing()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        await TestWebhooks.PostAsync(client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Completed, sessionId);

        // A different event id, so the duplicate check does not catch it: the guarded move does.
        var late = await TestWebhooks.PostAsync(client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Expired, sessionId);

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal(PaymentStatus.Succeeded, (await fixture.GetAttemptAsync(payment.Id)).Status);
        Assert.Equal(1, await fixture.CountOutboxAsync<PaymentSucceeded>(payment.OrderId));
        Assert.Equal(0, await fixture.CountOutboxAsync<PaymentFailed>(payment.OrderId));
    }

    [Fact]
    public async Task Completed_after_failed_changes_nothing_and_is_logged_as_a_warning()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        await TestWebhooks.PostAsync(client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Expired, sessionId);
        var completedId = $"evt_{Guid.NewGuid():N}";

        // Money was taken for a payment we had given up on. Nothing moves, and the log must say so.
        var response = await TestWebhooks.PostAsync(client, completedId, TestWebhooks.Completed, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PaymentStatus.Failed, (await fixture.GetAttemptAsync(payment.Id)).Status);
        Assert.Equal(0, await fixture.CountOutboxAsync<PaymentSucceeded>(payment.OrderId));
        var warning = Assert.Single(fixture.Logs.Snapshot(), l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && l.Message.Contains(completedId, StringComparison.Ordinal));
        Assert.Contains(payment.Id.ToString(), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_the_signature_is_checked()
    {
        using var client = fixture.CreateClient();

        // No signature at all: a signature check would answer 400. The size check comes first and answers 413.
        var response = await client.PostAsync("/webhooks/fakepay", new StringContent(new string('x', 20 * 1024)), Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_with_a_bad_signature_is_400_and_changes_nothing()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        var eventId = $"evt_{Guid.NewGuid():N}";

        var response = await TestWebhooks.PostAsync(client, eventId, TestWebhooks.Completed, sessionId, secret: "another-secret");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentStatus.Created, (await fixture.GetAttemptAsync(payment.Id)).Status);
        Assert.Equal(0, await fixture.CountOutboxAsync<PaymentSucceeded>(payment.OrderId));
        Assert.Equal(0, await fixture.QueryAsync(db => db.ProviderEvents.CountAsync(e => e.EventId == eventId, Ct)));
    }

    [Fact]
    public async Task Webhook_with_a_changed_body_is_400()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        var signed = TestWebhooks.Body("evt_1", TestWebhooks.Expired, sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/fakepay")
        {
            // Signed as "expired", sent as "completed".
            Content = new StringContent(TestWebhooks.Body("evt_1", TestWebhooks.Completed, sessionId)),
        };
        request.Headers.Add("FakePay-Signature", TestWebhooks.Sign(signed, DateTimeOffset.UtcNow));

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentStatus.Created, (await fixture.GetAttemptAsync(payment.Id)).Status);
    }

    [Theory]
    [InlineData("wrong,right", HttpStatusCode.OK)]
    [InlineData("right,wrong", HttpStatusCode.OK)]
    [InlineData("wrong,wrong", HttpStatusCode.BadRequest)]
    public async Task Webhook_is_accepted_when_any_v1_value_matches(string order, HttpStatusCode expected)
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();
        var body = TestWebhooks.Body($"evt_{Guid.NewGuid():N}", TestWebhooks.Completed, sessionId);
        // "t=<unix>,v1=<hex>" from the test signer; the header then carries two v1 values, as it does while a secret is rotated.
        var signed = TestWebhooks.Sign(body, DateTimeOffset.UtcNow).Split(',');
        var right = signed[1];
        var wrong = "v1=" + new string('0', 64);
        var v1Values = order.Split(',').Select(name => name == "right" ? right : wrong);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/fakepay")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("FakePay-Signature", string.Join(',', v1Values.Prepend(signed[0])));

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(
            expected == HttpStatusCode.OK ? PaymentStatus.Succeeded : PaymentStatus.Created,
            (await fixture.GetAttemptAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Webhook_without_a_signature_is_400()
    {
        var (_, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();

        var response = await client.PostAsync(
            "/webhooks/fakepay", new StringContent(TestWebhooks.Body("evt_2", TestWebhooks.Completed, sessionId)), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public async Task Webhook_older_than_five_minutes_is_400(int minutesFromNow)
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();

        // The signature is right for its timestamp; the timestamp is the problem. A replay of a captured request looks like this.
        var response = await TestWebhooks.PostAsync(
            client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Completed, sessionId, at: DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentStatus.Created, (await fixture.GetAttemptAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Webhook_a_little_old_is_accepted()
    {
        var (payment, sessionId) = await NewPaymentAsync();
        using var client = fixture.CreateClient();

        var response = await TestWebhooks.PostAsync(
            client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Completed, sessionId, at: DateTimeOffset.UtcNow.AddMinutes(-4));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PaymentStatus.Succeeded, (await fixture.GetAttemptAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Webhook_for_an_unknown_session_is_200_and_changes_nothing()
    {
        using var client = fixture.CreateClient();
        var eventId = $"evt_{Guid.NewGuid():N}";

        var response = await TestWebhooks.PostAsync(client, eventId, TestWebhooks.Completed, "cs_unknown");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Nothing is remembered, so the same event is looked at again when the session turns up.
        Assert.Equal(0, await fixture.QueryAsync(db => db.ProviderEvents.CountAsync(e => e.EventId == eventId, Ct)));
    }
}
