using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NewStore.Inventory;

/// <summary>
/// Optimistic concurrency: read the edition, decrement in memory, save. EF puts the row's <c>xmin</c> in the
/// WHERE of the UPDATE, so if anyone changed the row since the read, no row matches,
/// <see cref="DbUpdateConcurrencyException"/> is thrown and the whole attempt starts again from a fresh read.
/// The retries grow with contention; ADR 0007 measures them.
/// </summary>
public sealed class ConcurrencyTokenStrategy(
    InventoryDbContext db, TimeProvider time, IOptions<InventoryOptions> options, InventoryMetrics metrics)
    : IReserveStrategy
{
    public async Task<ReserveOutcome> ReserveAsync(
        Guid reservationId, Guid editionId, Guid buyerId, CancellationToken cancellationToken)
    {
        var (outcome, attempts) = await TryReserveAsync(reservationId, editionId, buyerId, cancellationToken);
        metrics.RecordReserve(outcome, attempts);
        return outcome;
    }

    private async Task<(ReserveOutcome Outcome, int Attempts)> TryReserveAsync(
        Guid reservationId, Guid editionId, Guid buyerId, CancellationToken ct)
    {
        // A request with this id may have been answered already, or lost on its way back: answer it again, take no copy.
        if (await ReserveSupport.FindByIdAsync(db, reservationId, editionId, buyerId, ct) is { } known)
        {
            return (known, 1);
        }

        // The failures below may be the result of a request with this same id that took the last copy since the lookup above.
        Task<ReserveOutcome> ExistingOrAsync(ReserveOutcome failure) =>
            ReserveSupport.ExistingOrAsync(db, reservationId, editionId, buyerId, failure, ct);

        var maxAttempts = options.Value.MaxReserveAttempts;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Forget the stale edition (and the reservation of the failed attempt) before reading again.
            db.ChangeTracker.Clear();

            var edition = await db.Editions.FirstOrDefaultAsync(e => e.Id == editionId, ct);
            if (edition is null)
            {
                return (await ExistingOrAsync(new ReserveOutcome.NotFound()), attempt);
            }

            if (edition.EndedAt is not null)
            {
                return (await ExistingOrAsync(new ReserveOutcome.SaleEnded()), attempt);
            }

            if (edition.CopiesLeft == 0)
            {
                return (await ExistingOrAsync(new ReserveOutcome.SoldOut()), attempt);
            }

            edition.CopiesLeft--;
            edition.LockedAt ??= time.GetUtcNow();
            var reservation = ReserveSupport.NewPending(reservationId, editionId, buyerId, time, options.Value);
            db.Reservations.Add(reservation);
            try
            {
                // One transaction: the UPDATE (guarded by xmin) and the INSERT commit together or not at all.
                await db.SaveChangesAsync(ct);
                return (new ReserveOutcome.Reserved(reservation), attempt);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else took a copy between our read and our write. Read again.
            }
            catch (DbUpdateException ex) when (ReserveSupport.IsReservationConflict(ex))
            {
                // SaveChanges rolled its own transaction back. Which constraint broke tells the outcome.
                return (await ReserveSupport.ResolveConflictAsync(db, reservationId, editionId, buyerId, ct), attempt);
            }
        }

        return (new ReserveOutcome.Contended(maxAttempts), maxAttempts);
    }
}
