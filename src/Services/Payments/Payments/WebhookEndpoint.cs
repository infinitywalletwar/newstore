using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Payments;
using NewStore.Messaging;
using NewStore.Payments.Provider;

namespace NewStore.Payments.Payments;

public static partial class WebhookEndpoint
{
    public static IEndpointRouteBuilder MapWebhookEndpoint(this IEndpointRouteBuilder routes)
    {
        // No token: the provider is not one of our clients. The signature is the check.
        routes.MapPost("/webhooks/fakepay", HandleAsync).AllowAnonymous();
        return routes;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request, IPaymentProvider provider, PaymentsDbContext db, IOutbox outbox, TimeProvider time,
        ILoggerFactory loggers, CancellationToken ct)
    {
        // The signature covers the exact bytes, so read the body as text and pass it on untouched.
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);

        // 1. A bad signature or an old timestamp: refuse, change nothing.
        if (provider.ReadWebhook(body, request.Headers) is not { } providerEvent)
        {
            return Problems.BadWebhook();
        }

        var logger = loggers.CreateLogger("NewStore.Payments.Webhooks");
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 2. Remember the event id. The primary key makes a second insert of the same id do nothing, even when two deliveries
        //    run at once: the second insert waits for the first transaction and then finds the key taken.
        var firstTime = await db.Database.ExecuteSqlAsync(
            $"INSERT INTO provider_events (event_id, received_at) VALUES ({providerEvent.EventId}, {now}) ON CONFLICT DO NOTHING", ct);
        if (firstTime == 0)
        {
            DuplicateIgnored(logger, providerEvent.EventId);
            return Results.Ok();
        }

        // 5. A session we do not know. Leaving the transaction uncommitted forgets the event id too, so the provider's next
        //    delivery of it is looked at again.
        var attempt = await db.PaymentAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.ProviderSessionId == providerEvent.SessionId, ct);
        if (attempt is null)
        {
            UnknownSession(logger, providerEvent.EventId, providerEvent.SessionId);
            return Results.Ok();
        }

        var (target, reason) = providerEvent.Type == ProviderEventType.SessionCompleted
            ? (PaymentStatus.Succeeded, (string?)null)
            : (PaymentStatus.Failed, "expired");

        // 3. A guarded move: the WHERE clause names the only state it may leave. Of two events that race (completed and
        //    expired), one finds the row in Created and the other finds nothing to update.
        var moved = await db.PaymentAttempts
            .Where(a => a.Id == attempt.Id && a.Status == PaymentStatus.Created)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.Status, target).SetProperty(a => a.UpdatedAt, now), ct);

        if (moved == 1)
        {
            // The event goes into the outbox in the same transaction as the move, so both exist or neither does.
            if (target == PaymentStatus.Succeeded)
            {
                outbox.Enqueue(
                    new PaymentSucceeded
                    {
                        EventId = Guid.CreateVersion7(),
                        OccurredAt = now,
                        PaymentId = attempt.Id,
                        OrderId = attempt.OrderId,
                        Amount = attempt.Amount,
                    },
                    PaymentEvents.Topic,
                    attempt.OrderId.ToString());
            }
            else
            {
                outbox.Enqueue(
                    new PaymentFailed
                    {
                        EventId = Guid.CreateVersion7(),
                        OccurredAt = now,
                        PaymentId = attempt.Id,
                        OrderId = attempt.OrderId,
                        Reason = reason!,
                    },
                    PaymentEvents.Topic,
                    attempt.OrderId.ToString());
            }

            await db.SaveChangesAsync(ct);
        }

        // 4. moved == 0: the attempt was not in Created, so nothing moved. The event id is still recorded, which is right: the
        //    event was handled, and its answer was "no change".
        await transaction.CommitAsync(ct);
        return Results.Ok();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Webhook {EventId} was already handled; nothing changed.")]
    private static partial void DuplicateIgnored(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {EventId} names session {SessionId}, which Payments does not know; nothing changed.")]
    private static partial void UnknownSession(ILogger logger, string eventId, string sessionId);
}
