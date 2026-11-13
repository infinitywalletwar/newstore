using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging;
using NewStore.Orders.Clients;
using NewStore.Orders.Orders;

namespace NewStore.Orders.Saga;

/// <summary>
/// The saga's reactions to what Payments reports (ADR 0017). Each handler runs in the inbox transaction, once per message id, and
/// every call it makes to Inventory is safe to repeat, because the transaction can still fail after the call went through.
/// </summary>
internal static class SagaOrders
{
    public static async Task<Order> LoadAsync(OrdersDbContext db, Guid orderId, CancellationToken ct) =>
        await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            // Retrying cannot make an unknown order appear: the message is dead-lettered.
            ?? throw new InvalidOperationException($"A payment event names the unknown order {orderId}.");
}

/// <summary>The money arrived: take the copy, or give the money back.</summary>
public sealed class PaymentSucceededHandler(
    OrdersDbContext db, InventoryClient inventory, IOutbox outbox, TimeProvider time) : IIntegrationEventHandler<PaymentSucceeded>
{
    public async Task HandleAsync(PaymentSucceeded integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var order = await SagaOrders.LoadAsync(db, integrationEvent.OrderId, cancellationToken);
        var now = time.GetUtcNowToMicrosecond();

        switch (order.Status)
        {
            case OrderStatus.Reserved or OrderStatus.Paying:
                // A transient failure leaves this method: the message is retried while Inventory is down.
                switch (await inventory.ConfirmAsync(order.ReservationId, cancellationToken))
                {
                    case ConfirmResult.Confirmed confirmed:
                        order.Status = OrderStatus.Completed;
                        order.CopyNumber = confirmed.CopyNumber;
                        order.PaymentId = integrationEvent.PaymentId;
                        order.CompletedAt = now;
                        order.UpdatedAt = now;
                        break;
                    default:
                        Refund(order, integrationEvent.PaymentId, now);
                        break;
                }

                break;

            case OrderStatus.Expired or OrderStatus.Failed:
                // The buyer paid for an order that already ended: no copy, so the money goes back.
                Refund(order, integrationEvent.PaymentId, now);
                break;

            default:
                // Completed, Refunding or Refunded: a repeat of an event already acted on.
                break;
        }
    }

    private void Refund(Order order, Guid paymentId, DateTimeOffset now)
    {
        order.Status = OrderStatus.Refunding;
        order.PaymentId = paymentId;
        order.UpdatedAt = now;
        // Written with the status change, so Orders never records "Refunding" without asking for the refund.
        outbox.Enqueue(
            new RefundRequested { EventId = Guid.CreateVersion7(), OccurredAt = now, OrderId = order.Id, PaymentId = paymentId },
            OrderEvents.Topic,
            order.Id.ToString());
    }
}

/// <summary>The payment ended without money moving, for example because the provider's session expired: give the copy back.</summary>
public sealed class PaymentFailedHandler(OrdersDbContext db, InventoryClient inventory, TimeProvider time)
    : IIntegrationEventHandler<PaymentFailed>
{
    public async Task HandleAsync(PaymentFailed integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var order = await SagaOrders.LoadAsync(db, integrationEvent.OrderId, cancellationToken);

        if (order.Status is OrderStatus.Reserved or OrderStatus.Paying)
        {
            await inventory.ReleaseAsync(order.ReservationId, cancellationToken);
            order.Status = OrderStatus.Failed;
            order.UpdatedAt = time.GetUtcNowToMicrosecond();
        }
    }
}

/// <summary>Payments gave the money back: the order is closed.</summary>
public sealed class PaymentRefundedHandler(OrdersDbContext db, TimeProvider time) : IIntegrationEventHandler<PaymentRefunded>
{
    public async Task HandleAsync(PaymentRefunded integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var order = await SagaOrders.LoadAsync(db, integrationEvent.OrderId, cancellationToken);

        if (order.Status == OrderStatus.Refunding)
        {
            order.Status = OrderStatus.Refunded;
            order.UpdatedAt = time.GetUtcNowToMicrosecond();
        }
    }
}
