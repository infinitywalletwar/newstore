using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Orders.Orders;

namespace NewStore.Orders.Tests;

[Collection("orders")]
public class SagaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OrdersFixture _fixture;

    public SagaTests(OrdersFixture fixture)
    {
        _fixture = fixture;
        fixture.Reset();
    }

    private static PaymentSucceeded Succeeded(Order order, Guid? paymentId = null) => new()
    {
        EventId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order.Id,
        PaymentId = paymentId ?? order.PaymentId ?? Guid.NewGuid(),
        Amount = order.Price,
    };

    private Task PublishAsync<TEvent>(Order order, TEvent @event)
        where TEvent : NewStore.Messaging.IIntegrationEvent =>
        _fixture.Kafka.PublishAsync(PaymentEvents.Topic, order.Id.ToString(), @event, Ct);

    private static PaymentFailed Failed(Order order) => new()
    {
        EventId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order.Id,
        PaymentId = order.PaymentId ?? Guid.NewGuid(),
        Reason = "session expired",
    };

    private int Calls(Order order, string action) =>
        _fixture.Inventory.RequestsFor(order.ReservationId).Count(r => r.RequestUri!.AbsolutePath.EndsWith("/" + action, StringComparison.Ordinal));

    [Fact]
    public async Task PaymentSucceeded_confirms_and_completes_the_order_with_the_copy_number()
    {
        var paying = await _fixture.PayingOrderAsync();

        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Completed);

        var order = await _fixture.GetOrderAsync(paying.Id);
        Assert.Equal(1, Calls(paying, "confirm"));
        Assert.NotNull(order.CopyNumber);
        Assert.NotNull(order.CompletedAt);
        Assert.Equal(paying.PaymentId, order.PaymentId);

        // The buyer sees the copy on the order page.
        using var client = _fixture.BuyerClient(paying.BuyerId);
        var response = await client.GetFromJsonAsync<OrderResponse>($"/orders/{paying.Id}", Ct);
        Assert.Equal("Completed", response!.Status);
        Assert.Equal(order.CopyNumber, response.CopyNumber);
    }

    [Fact]
    public async Task Late_PaymentSucceeded_ends_in_a_refund()
    {
        var paying = await _fixture.PayingOrderAsync();
        // The hold ran out before the money arrived: Inventory refuses the confirm.
        _fixture.Inventory.Confirm = _ => StubReplies.Problem(HttpStatusCode.Conflict, "reservation-expired");

        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Refunding);

        var request = Assert.Single(await _fixture.RefundRequestsAsync(paying.Id));
        Assert.Equal(paying.PaymentId, request.PaymentId);
        var order = await _fixture.GetOrderAsync(paying.Id);
        Assert.Null(order.CopyNumber);

        // Payments gives the money back and says so.
        await PublishAsync(paying, new PaymentRefunded
        {
            EventId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = paying.Id,
            PaymentId = paying.PaymentId!.Value,
            Amount = paying.Price,
        });
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Refunded);
    }

    [Fact]
    public async Task PaymentSucceeded_for_an_expired_order_ends_in_a_refund()
    {
        var buyer = Guid.NewGuid();
        var reserved = await _fixture.BuyAsync(buyer);
        _fixture.Inventory.Hold = _ => StubReplies.Problem(HttpStatusCode.Conflict, "reservation-expired");
        using (var client = _fixture.BuyerClient(buyer))
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/orders/{reserved.Id}/pay", null, Ct)).StatusCode);
        }

        var expired = await _fixture.GetOrderAsync(reserved.Id);
        Assert.Equal(OrderStatus.Expired, expired.Status);
        await PublishAsync(expired, Succeeded(expired));
        await _fixture.WaitForStatusAsync(expired.Id, OrderStatus.Refunding);

        // No call to confirm: an expired order has no reservation to confirm.
        Assert.Equal(0, Calls(expired, "confirm"));
        Assert.Equal(1, await _fixture.CountOutboxAsync<RefundRequested>(expired.Id));
    }

    [Fact]
    public async Task PaymentFailed_releases_the_reservation_and_fails_the_order()
    {
        var paying = await _fixture.PayingOrderAsync();

        await PublishAsync(paying, new PaymentFailed
        {
            EventId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = paying.Id,
            PaymentId = paying.PaymentId!.Value,
            Reason = "session expired",
        });
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Failed);

        Assert.Equal(1, Calls(paying, "release"));
        Assert.Equal(0, Calls(paying, "confirm"));
    }

    [Fact]
    public async Task PaymentSucceeded_for_a_failed_order_ends_in_a_refund_without_asking_inventory()
    {
        var paying = await _fixture.PayingOrderAsync();
        var failed = Failed(paying);
        await PublishAsync(paying, failed);
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Failed);

        // The payment succeeded after the provider's session had already expired.
        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Refunding);

        Assert.Equal(0, Calls(paying, "confirm"));
        // The event asks for the money of the payment that succeeded, not only "a" refund.
        var request = Assert.Single(await _fixture.RefundRequestsAsync(paying.Id));
        Assert.Equal(paying.PaymentId, request.PaymentId);
        Assert.Equal(paying.Id, request.OrderId);
    }

    [Fact]
    public async Task PaymentFailed_on_a_reserved_order_releases_and_fails_it()
    {
        var reserved = await _fixture.GetOrderAsync((await _fixture.BuyAsync(Guid.NewGuid())).Id);

        await PublishAsync(reserved, Failed(reserved));
        await _fixture.WaitForStatusAsync(reserved.Id, OrderStatus.Failed);

        Assert.Equal(1, Calls(reserved, "release"));
    }

    [Fact]
    public async Task PaymentFailed_on_a_completed_order_changes_nothing()
    {
        var paying = await _fixture.PayingOrderAsync();
        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Completed);
        var failed = Failed(paying);

        await PublishAsync(paying, failed);
        await _fixture.WaitHandledAsync(failed.EventId);

        Assert.Equal(OrderStatus.Completed, (await _fixture.GetOrderAsync(paying.Id)).Status);
        Assert.Equal(0, Calls(paying, "release"));
    }

    [Fact]
    public async Task Repeated_payment_events_while_refunding_or_refunded_change_nothing()
    {
        var paying = await _fixture.PayingOrderAsync();
        _fixture.Inventory.Confirm = _ => StubReplies.Problem(HttpStatusCode.Conflict, "reservation-expired");
        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Refunding);

        // A second PaymentSucceeded (a different message) while Refunding: no second refund request.
        var again = Succeeded(paying);
        await PublishAsync(paying, again);
        await _fixture.WaitHandledAsync(again.EventId);
        Assert.Equal(OrderStatus.Refunding, (await _fixture.GetOrderAsync(paying.Id)).Status);
        Assert.Equal(1, await _fixture.CountOutboxAsync<RefundRequested>(paying.Id));

        var refunded = new PaymentRefunded
        {
            EventId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = paying.Id,
            PaymentId = paying.PaymentId!.Value,
            Amount = paying.Price,
        };
        await PublishAsync(paying, refunded);
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Refunded);

        // The same again, then another PaymentSucceeded, once Refunded.
        var late = Succeeded(paying);
        await PublishAsync(paying, refunded);
        await PublishAsync(paying, late);
        await _fixture.WaitHandledAsync(late.EventId);
        Assert.Equal(OrderStatus.Refunded, (await _fixture.GetOrderAsync(paying.Id)).Status);
        Assert.Equal(1, await _fixture.CountOutboxAsync<RefundRequested>(paying.Id));
    }

    [Fact]
    public async Task Duplicate_PaymentSucceeded_confirms_once()
    {
        var paying = await _fixture.PayingOrderAsync();
        var first = Succeeded(paying);

        // The same message twice (Kafka delivers at least once), then a second message for the same payment.
        await PublishAsync(paying, first);
        await PublishAsync(paying, first);
        var second = Succeeded(paying);
        await PublishAsync(paying, second);
        // Same key, same partition: the second message is handled after the duplicate, so waiting for it proves the duplicate was seen.
        await OrdersFixture.WaitAsync(
            () => _fixture.QueryAsync(db => db.Set<NewStore.Messaging.Inbox.InboxMessage>().AnyAsync(m => m.MessageId == second.EventId, Ct)),
            "the second message is handled");

        Assert.Equal(1, Calls(paying, "confirm"));
        Assert.Equal(OrderStatus.Completed, (await _fixture.GetOrderAsync(paying.Id)).Status);
        Assert.Equal(0, await _fixture.CountOutboxAsync<RefundRequested>(paying.Id));
    }

    [Fact]
    public async Task PaymentSucceeded_waits_while_inventory_is_down_and_completes_later()
    {
        var paying = await _fixture.PayingOrderAsync();
        var original = _fixture.Inventory.Confirm;
        var until = DateTime.UtcNow.AddSeconds(10);
        var failures = 0;
        _fixture.Inventory.Confirm = r =>
        {
            if (DateTime.UtcNow < until)
            {
                Interlocked.Increment(ref failures);
                return StubReplies.Unavailable();
            }

            return original(r);
        };
        var message = Succeeded(paying);

        await PublishAsync(paying, message);
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Completed);

        // Each handler run makes two requests (one retry). More than MaxHandlerAttempts (1) runs failed and the message survived:
        // a failure that was not classified as transient would have been dead-lettered after the first.
        Assert.True(failures > 2, $"Inventory saw {failures} failing confirm requests.");
        Assert.DoesNotContain(message.EventId, await DeadLetteredIdsAsync(PaymentEvents.Topic));
    }

    [Fact]
    public async Task A_payment_event_for_an_unknown_order_is_dead_lettered()
    {
        var unknown = new Order { Id = Guid.NewGuid(), Format = "Hd", Price = 1m };
        var message = Succeeded(unknown);

        await PublishAsync(unknown, message);

        await OrdersFixture.WaitAsync(
            async () => (await DeadLetteredIdsAsync(PaymentEvents.Topic)).Contains(message.EventId), "the event is dead-lettered");
    }

    [Fact]
    public async Task My_copies_lists_completed_orders_only()
    {
        var buyer = Guid.NewGuid();
        var completed = await _fixture.PayingOrderAsync(buyer);
        await PublishAsync(completed, Succeeded(completed));
        await _fixture.WaitForStatusAsync(completed.Id, OrderStatus.Completed);
        var paying = await _fixture.PayingOrderAsync(buyer);
        var reserved = await _fixture.BuyAsync(buyer);

        using var client = _fixture.BuyerClient(buyer);
        var copies = await client.GetFromJsonAsync<JsonElement>("/me/copies", Ct);

        var copy = Assert.Single(copies.EnumerateArray());
        Assert.Equal(completed.Id, copy.GetProperty("orderId").GetGuid());
        // The names Task 8's models.ts reads.
        Assert.Equal(
            ["completedAt", "copyNumber", "deliveryStatus", "editionId", "format", "orderId", "price", "videoId"],
            copy.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("deliveryStatus").ValueKind);
        Assert.NotEqual(paying.Id, copy.GetProperty("orderId").GetGuid());
        Assert.NotEqual(reserved.Id, copy.GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task My_copies_lists_newest_first_and_only_the_callers()
    {
        var buyer = Guid.NewGuid();
        var older = await _fixture.PayingOrderAsync(buyer);
        await PublishAsync(older, Succeeded(older));
        await _fixture.WaitForStatusAsync(older.Id, OrderStatus.Completed);
        var newer = await _fixture.PayingOrderAsync(buyer);
        await PublishAsync(newer, Succeeded(newer));
        await _fixture.WaitForStatusAsync(newer.Id, OrderStatus.Completed);

        using var client = _fixture.BuyerClient(buyer);
        var copies = await client.GetFromJsonAsync<CopyResponse[]>("/me/copies", Ct);
        using var stranger = _fixture.BuyerClient(Guid.NewGuid());
        var none = await stranger.GetFromJsonAsync<CopyResponse[]>("/me/copies", Ct);

        Assert.Equal([newer.Id, older.Id], copies!.Select(c => c.OrderId));
        Assert.Empty(none!);
    }

    /// <summary>The message ids on <c>&lt;topic&gt;.dlt</c>, read from the start.</summary>
    private Task<List<Guid>> DeadLetteredIdsAsync(string topic) => Task.Run(() => ReadDeadLetteredIds(topic), Ct);

    private List<Guid> ReadDeadLetteredIds(string topic)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _fixture.Kafka.BootstrapServers,
            GroupId = $"dlt-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(topic + ".dlt");
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
