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
        var logger = loggers.CreateLogger("NewStore.Payments.Webhooks");

        // The endpoint is anonymous, so anyone can send a body of any size. Refuse a large one before reading it all, and before any
        // signature work.
        if (await ReadBodyAsync(request, ct) is not { } body)
        {
            return Problems.BodyTooLarge();
        }

        // 1. A bad signature, a stale timestamp or an unknown type: refuse, change nothing.
        if (provider.ReadWebhook(body, request.Headers) is not { } providerEvent)
        {
            return Problems.BadWebhook();
        }

        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 2. Remember the event id. The primary key makes a second insert of the same id do nothing, even when two deliveries
        //    run at once. PostgreSQL: "If a conflicting row has been inserted by an as-yet-uncommitted transaction, the would-be
        //    inserter must wait to see if that transaction commits. If it rolls back then there is no conflict. If it commits
        //    without deleting the conflicting row again, there is a uniqueness violation"
        //    (https://www.postgresql.org/docs/current/index-unique-checks.html); with ON CONFLICT DO NOTHING, that violation
        //    becomes "no row inserted". The test Ten_parallel_deliveries_of_one_webhook_publish_once runs it.
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
        //    event was handled, and its answer was "no change". One such case needs a person: the provider took money for a
        //    payment we had already failed.
        if (moved == 0 && providerEvent.Type == ProviderEventType.SessionCompleted)
        {
            CompletedButNotCreated(logger, providerEvent.EventId, attempt.Id, attempt.Status);
        }

        await transaction.CommitAsync(ct);
        return Results.Ok();
    }

    /// <summary>A webhook is a few hundred bytes. 16 KiB leaves room for a longer id or more fields and stops a flood of large bodies.</summary>
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// The body as text, or null when it is larger than <see cref="MaxBodyBytes"/>. The signature covers the exact bytes, so the
    /// text is handed on untouched.
    /// </summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        // A body without a Content-Length (chunked) is counted as it is read.
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {EventId} says payment {PaymentId} was paid, but the payment is {Status}, so nothing moved. Money may have been taken for a payment we gave up on.")]
    private static partial void CompletedButNotCreated(ILogger logger, string eventId, Guid paymentId, PaymentStatus status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Webhook {EventId} was already handled; nothing changed.")]
    private static partial void DuplicateIgnored(ILogger logger, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook {EventId} names session {SessionId}, which Payments does not know; nothing changed.")]
    private static partial void UnknownSession(ILogger logger, string eventId, string sessionId);
}
