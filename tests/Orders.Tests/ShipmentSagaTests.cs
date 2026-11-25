using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging.Outbox;
using NewStore.Orders.Orders;

namespace NewStore.Orders.Tests;

/// <summary>A completed DVD order asks Delivery to ship it, in the same transaction as the status change.</summary>
[Collection("orders")]
public class ShipmentSagaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OrdersFixture _fixture;

    public ShipmentSagaTests(OrdersFixture fixture)
    {
        _fixture = fixture;
        fixture.Reset();
    }

    private static PaymentSucceeded Succeeded(Order order) => new()
    {
        EventId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order.Id,
        PaymentId = order.PaymentId ?? Guid.NewGuid(),
        Amount = order.Price,
    };

    private Task PublishAsync(Order order, PaymentSucceeded @event) =>
        _fixture.Kafka.PublishAsync(PaymentEvents.Topic, order.Id.ToString(), @event, Ct);

    /// <summary>Buys a DVD with an address through the endpoint and pays: the order is <c>Paying</c>.</summary>
    private async Task<Order> PayingDvdOrderAsync()
    {
        _fixture.Inventory.Format = "Dvd";
        var buyer = Guid.NewGuid();
        using var client = _fixture.BuyerClient(buyer);
        var buy = await client.PostAsJsonAsync(
            "/orders",
            new { editionId = Guid.NewGuid(), delivery = new { address = "Alexanderplatz 1, 10178 Berlin", latitude = 52.5219, longitude = 13.4132 } },
            Ct);
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);
        var order = (await buy.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/orders/{order.Id}/pay", null, Ct)).StatusCode);
        return await _fixture.GetOrderAsync(order.Id);
    }

    private Task<List<OutboxMessage>> ShipmentRowsAsync(Guid orderId) =>
        _fixture.QueryAsync(db => db.Set<OutboxMessage>()
            .Where(m => m.Type == typeof(ShipmentRequested).FullName && m.Key == orderId.ToString())
            .ToListAsync(Ct));

    [Fact]
    public async Task Completed_dvd_order_enqueues_ShipmentRequested_with_the_address()
    {
        var paying = await PayingDvdOrderAsync();

        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Completed);

        var completed = await _fixture.GetOrderAsync(paying.Id);
        var row = Assert.Single(await ShipmentRowsAsync(paying.Id));
        Assert.Equal("NewStore.Contracts.Orders.ShipmentRequested", row.Type);
        Assert.Equal(OrderEvents.Topic, row.Topic);
        var json = JsonDocument.Parse(row.Payload).RootElement;
        Assert.Equal(paying.Id, json.GetProperty("OrderId").GetGuid());
        Assert.Equal(paying.BuyerId, json.GetProperty("BuyerId").GetGuid());
        Assert.Equal(paying.EditionId, json.GetProperty("EditionId").GetGuid());
        Assert.Equal(paying.VideoId, json.GetProperty("VideoId").GetGuid());
        Assert.Equal(completed.CopyNumber, json.GetProperty("CopyNumber").GetInt32());
        Assert.Equal("Alexanderplatz 1, 10178 Berlin", json.GetProperty("Address").GetString());
        Assert.Equal(52.5219, json.GetProperty("Latitude").GetDouble());
        Assert.Equal(13.4132, json.GetProperty("Longitude").GetDouble());
    }

    [Fact]
    public async Task Completed_digital_order_enqueues_no_shipment()
    {
        var paying = await _fixture.PayingOrderAsync();

        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Completed);

        Assert.Empty(await ShipmentRowsAsync(paying.Id));
    }

    [Fact]
    public async Task Duplicate_PaymentSucceeded_enqueues_one_shipment()
    {
        var paying = await PayingDvdOrderAsync();
        var first = Succeeded(paying);

        // The same message twice (the inbox drops it), and a second event with a new id (the status check drops it).
        await PublishAsync(paying, first);
        await PublishAsync(paying, first);
        var second = Succeeded(paying);
        await PublishAsync(paying, second);
        await _fixture.WaitHandledAsync(first.EventId);
        await _fixture.WaitHandledAsync(second.EventId);

        Assert.Single(await ShipmentRowsAsync(paying.Id));
    }

    [Fact]
    public async Task Late_payment_for_a_dvd_order_refunds_and_requests_no_shipment()
    {
        var paying = await PayingDvdOrderAsync();
        _fixture.Inventory.Confirm = _ => StubReplies.Problem(HttpStatusCode.Conflict, "reservation-expired");

        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Refunding);

        Assert.Equal(1, await _fixture.CountOutboxAsync<RefundRequested>(paying.Id));
        Assert.Empty(await ShipmentRowsAsync(paying.Id));
    }

    [Fact]
    public async Task Dvd_order_without_an_address_completes_without_a_shipment()
    {
        // An order saved before this change: a DVD with no delivery fields.
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            BuyerId = Guid.NewGuid(),
            EditionId = Guid.NewGuid(),
            VideoId = Guid.NewGuid(),
            Format = EditionFormats.Dvd,
            Price = 9.99m,
            ReservationId = Guid.NewGuid(),
            ReservedUntil = now.AddMinutes(30),
            Status = OrderStatus.Paying,
            PaymentId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _fixture.QueryAsync(async db =>
        {
            db.Orders.Add(order);
            return await db.SaveChangesAsync(Ct);
        });

        await PublishAsync(order, Succeeded(order));
        await _fixture.WaitForStatusAsync(order.Id, OrderStatus.Completed);

        Assert.Empty(await ShipmentRowsAsync(order.Id));
        var warning = Assert.Single(
            _fixture.Logs.Snapshot(),
            l => l.Level == LogLevel.Warning && l.Message.Contains(order.Id.ToString(), StringComparison.Ordinal));
        Assert.Contains("no delivery address", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task My_copies_has_no_delivery_status()
    {
        var paying = await PayingDvdOrderAsync();
        await PublishAsync(paying, Succeeded(paying));
        await _fixture.WaitForStatusAsync(paying.Id, OrderStatus.Completed);

        using var client = _fixture.BuyerClient(paying.BuyerId);
        var copies = await client.GetFromJsonAsync<JsonElement>("/me/copies", Ct);

        var copy = Assert.Single(copies.EnumerateArray());
        Assert.False(copy.TryGetProperty("deliveryStatus", out _));
    }
}
