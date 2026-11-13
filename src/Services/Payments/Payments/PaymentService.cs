using Microsoft.EntityFrameworkCore;

using NewStore.Payments.Provider;

namespace NewStore.Payments.Payments;

/// <summary>Creates a payment and its provider session, so that any number of calls for one order end with one of each.</summary>
public sealed class PaymentService(PaymentsDbContext db, IPaymentProvider provider, TimeProvider time)
{
    /// <summary>
    /// The payment for <paramref name="orderId"/>, with a provider session. <c>Created</c> is true only for the call that inserted the
    /// attempt. Throws a transient exception when the provider cannot be reached; the attempt then stays, without a session, and a
    /// repeated call finishes it with the same idempotency key.
    /// </summary>
    public async Task<(PaymentAttempt Attempt, bool Created)> CreateAsync(Guid orderId, decimal amount, string returnUrl, CancellationToken ct)
    {
        // The unique index on order_id decides which of several parallel calls inserts. The others insert nothing and no error is raised
        // (ON CONFLICT DO NOTHING), then read the winner's row.
        var now = time.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO payment_attempts (id, order_id, amount, status, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {orderId}, {amount}, {nameof(PaymentStatus.Created)}, {now}, {now})
            ON CONFLICT (order_id) DO NOTHING
            """,
            ct);

        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.OrderId == orderId, ct);
        if (attempt.ProviderSessionId is null)
        {
            attempt = await EnsureSessionAsync(attempt.Id, returnUrl, ct);
        }

        return (attempt, inserted == 1);
    }

    /// <summary>
    /// Locks the attempt's row, and then looks again before calling the provider: a call that waited for the lock finds the session
    /// the first call stored and makes no request. The cost: a pooled connection, the row lock and the transaction stay for the whole provider call. That call has its own pipeline (<see cref="FakePayProviderRegistration"/>): at most one retry, 2 s per attempt and 5 s in all by default, so the lock is released well inside the caller's 10 s attempt timeout.
    /// </summary>
    private async Task<PaymentAttempt> EnsureSessionAsync(Guid attemptId, string returnUrl, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payment_attempts WHERE id = {attemptId} FOR UPDATE", ct);

        var attempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, ct);
        if (attempt.ProviderSessionId is not null)
        {
            return attempt;
        }

        var session = await provider.CreateSessionAsync(attempt, successUrl: returnUrl, cancelUrl: returnUrl, ct);
        var now = time.GetUtcNow();
        await db.PaymentAttempts.Where(a => a.Id == attemptId).ExecuteUpdateAsync(
            set => set
                .SetProperty(a => a.ProviderSessionId, session.SessionId)
                .SetProperty(a => a.CheckoutUrl, session.CheckoutUrl)
                .SetProperty(a => a.SessionExpiresAt, session.ExpiresAt)
                .SetProperty(a => a.UpdatedAt, now),
            ct);
        await transaction.CommitAsync(ct);

        return await db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, ct);
    }
}
