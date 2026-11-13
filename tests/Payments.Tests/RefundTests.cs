using System.Text;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging.Inbox;
using NewStore.Payments.Payments;

namespace NewStore.Payments.Tests;

[Collection("payments")]
public class RefundTests(PaymentsFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(PaymentResponse Payment, string SessionId)> SucceededPaymentAsync()
    {
        var payment = await fixture.CreatePaymentAsync();
        var sessionId = (await fixture.GetAttemptAsync(payment.Id)).ProviderSessionId!;
        using var client = fixture.CreateClient();
        var response = await TestWebhooks.PostAsync(client, $"evt_{Guid.NewGuid():N}", TestWebhooks.Completed, sessionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (payment, sessionId);
    }

    private static RefundRequested Request(PaymentResponse payment) => new()
    {
        EventId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = payment.OrderId,
        PaymentId = payment.Id,
    };

    private Task<bool> HandledAsync(Guid eventId) =>
        fixture.QueryAsync(db => db.Set<InboxMessage>().AnyAsync(m => m.MessageId == eventId, Ct));

    [Fact]
    public async Task RefundRequested_refunds_once_and_publishes_PaymentRefunded()
    {
        var (payment, sessionId) = await SucceededPaymentAsync();
        var request = Request(payment);
        var key = payment.OrderId.ToString();

        // The same message twice: Kafka delivers at least once.
        await fixture.Kafka.PublishAsync(OrderEvents.Topic, key, request, Ct);
        await fixture.Kafka.PublishAsync(OrderEvents.Topic, key, request, Ct);
        // A second request for the same payment, with its own id. It reaches the handler and finds the payment refunded.
        // Same key, same partition, so it is handled after the duplicate: waiting for it proves the duplicate was seen.
        var second = Request(payment);
        await fixture.Kafka.PublishAsync(OrderEvents.Topic, key, second, Ct);
        await PaymentsFixture.WaitAsync(() => HandledAsync(second.EventId), "the second request is handled");

        Assert.Equal(PaymentStatus.Refunded, (await fixture.GetAttemptAsync(payment.Id)).Status);
        var refund = Assert.Single(fixture.Provider.RefundRequestsFor(sessionId));
        Assert.Equal(payment.Id.ToString(), refund.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal(1, await fixture.CountOutboxAsync<PaymentRefunded>(payment.OrderId));
    }

    [Fact]
    public async Task Refund_waits_while_the_provider_is_down()
    {
        var (payment, _) = await SucceededPaymentAsync();
        var request = Request(payment);
        var original = fixture.Provider.Refund;
        var calls = 0;
        // Down for six requests, then up. The refund client retries once, so each handler run makes two requests: the handler runs
        // three times and fails twice more than the fixture's MaxHandlerAttempts (1) would allow for an ordinary failure.
        fixture.Provider.Refund = r =>
            Interlocked.Increment(ref calls) <= 6 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : original(r);
        try
        {
            await fixture.Kafka.PublishAsync(OrderEvents.Topic, payment.OrderId.ToString(), request, Ct);

            await PaymentsFixture.WaitAsync(
                async () => (await fixture.GetAttemptAsync(payment.Id)).Status == PaymentStatus.Refunded, "the payment is refunded");
        }
        finally
        {
            fixture.Provider.Refund = original;
        }

        Assert.True(calls >= 7, $"The provider saw {calls} refund requests.");
        Assert.Equal(1, await fixture.CountOutboxAsync<PaymentRefunded>(payment.OrderId));
        Assert.DoesNotContain(request.EventId, await DeadLetteredIdsAsync());
    }

    [Fact]
    public async Task RefundRequested_for_a_payment_that_never_succeeded_is_dead_lettered()
    {
        var payment = await fixture.CreatePaymentAsync();
        var request = Request(payment);

        await fixture.Kafka.PublishAsync(OrderEvents.Topic, payment.OrderId.ToString(), request, Ct);

        await PaymentsFixture.WaitAsync(async () => (await DeadLetteredIdsAsync()).Contains(request.EventId), "the request is dead-lettered");
        Assert.Equal(PaymentStatus.Created, (await fixture.GetAttemptAsync(payment.Id)).Status);
    }

    /// <summary>The message ids on <c>orders.orders.dlt</c>, read from the start.</summary>
    private Task<List<Guid>> DeadLetteredIdsAsync() => Task.Run(ReadDeadLetteredIds, Ct);

    private List<Guid> ReadDeadLetteredIds()
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = fixture.Kafka.BootstrapServers,
            GroupId = $"dlt-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(OrderEvents.Topic + ".dlt");
        var ids = new List<Guid>();
        var quiet = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < quiet)
        {
            if (consumer.Consume(TimeSpan.FromMilliseconds(300)) is { } result
                && result.Message.Headers.TryGetLastBytes("message-id", out var bytes)
                && Guid.TryParse(Encoding.UTF8.GetString(bytes), out var id))
            {
                ids.Add(id);
                quiet = DateTime.UtcNow.AddSeconds(1);
            }
        }

        return ids;
    }
}
