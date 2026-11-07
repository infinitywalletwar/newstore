using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NewStore.Inventory;

/// <summary>
/// One <c>UPDATE editions SET copies_left = copies_left - 1, locked_at = COALESCE(locked_at, @now)
/// WHERE id = … AND copies_left &gt; 0 AND ended_at IS NULL</c>. The first reserve sets <c>locked_at</c>; later ones keep it. PostgreSQL locks
/// the row for the update, so concurrent reserves of one edition queue on the row, and each one sees the
/// count the one before it left. There is nothing to retry.
/// </summary>
public sealed class ConditionalUpdateStrategy(
    InventoryDbContext db, TimeProvider time, IOptions<InventoryOptions> options, InventoryMetrics metrics)
    : IReserveStrategy
{
    public async Task<ReserveOutcome> ReserveAsync(Guid editionId, Guid buyerId, CancellationToken cancellationToken)
    {
        var outcome = await TryReserveAsync(editionId, buyerId, cancellationToken);
        metrics.RecordReserve(outcome, attempts: 1);
        return outcome;
    }

    private async Task<ReserveOutcome> TryReserveAsync(Guid editionId, Guid buyerId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = time.GetUtcNow();
        var decremented = await db.Editions
            .Where(e => e.Id == editionId && e.CopiesLeft > 0 && e.EndedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.CopiesLeft, e => e.CopiesLeft - 1)
                .SetProperty(e => e.LockedAt, e => e.LockedAt ?? now), ct);
        if (decremented == 0)
        {
            var edition = await db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == editionId, ct);
            return edition switch
            {
                null => new ReserveOutcome.NotFound(),
                { EndedAt: not null } => new ReserveOutcome.SaleEnded(),
                _ => new ReserveOutcome.SoldOut(),
            };
        }

        var reservation = ReserveSupport.NewPending(editionId, buyerId, time, options.Value);
        db.Reservations.Add(reservation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ReserveSupport.IsAlreadyHolding(ex))
        {
            // Leaving without a commit rolls the transaction back, and the decrement with it.
            return new ReserveOutcome.AlreadyHolding();
        }

        await transaction.CommitAsync(ct);
        return new ReserveOutcome.Reserved(reservation);
    }
}
