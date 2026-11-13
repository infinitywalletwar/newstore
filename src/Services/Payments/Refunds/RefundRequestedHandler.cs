using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Orders;
using NewStore.Contracts.Payments;
using NewStore.Messaging;
using NewStore.Payments.Payments;
using NewStore.Payments.Provider;

namespace NewStore.Payments.Refunds;

/// <summary>
/// Orders asks for the money back when a payment succeeded after the reservation's hold ran out. The handler runs in the inbox
/// transaction, once per message id. It still has to cope with a second request for the same payment (a different message), and
/// with the provider call succeeding while the commit fails: the provider's idempotency key, the payment id, makes that repeat refund nothing.
/// </summary>
public sealed class RefundRequestedHandler(PaymentsDbContext db, IPaymentProvider provider, IOutbox outbox, TimeProvider time)
    : IIntegrationEventHandler<RefundRequested>
{
    public async Task HandleAsync(RefundRequested integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var attempt = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.Id == integrationEvent.PaymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Refund requested for unknown payment {integrationEvent.PaymentId}.");

        switch (attempt.Status)
        {
            case PaymentStatus.Refunded:
                return;

            case PaymentStatus.Succeeded:
                // HttpRequestException and TransientFailureException leave this method: Messaging retries the message.
                await provider.RefundAsync(attempt, cancellationToken);

                var now = time.GetUtcNow();
                attempt.Status = PaymentStatus.Refunded;
                attempt.UpdatedAt = now;
                outbox.Enqueue(
                    new PaymentRefunded
                    {
                        EventId = Guid.CreateVersion7(),
                        OccurredAt = now,
                        PaymentId = attempt.Id,
                        OrderId = attempt.OrderId,
                        Amount = attempt.Amount,
                    },
                    PaymentEvents.Topic,
                    attempt.OrderId.ToString());
                return;

            default:
                // Created or Failed: nothing was paid, so there is nothing to refund. Retrying cannot change that: dead-letter it.
                throw new InvalidOperationException($"Refund requested for payment {attempt.Id}, which is {attempt.Status}.");
        }
    }
}
