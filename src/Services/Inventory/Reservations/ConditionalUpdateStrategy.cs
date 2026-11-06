using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NewStore.Inventory;

/// <summary>
/// One <c>UPDATE editions SET copies_left = copies_left - 1 WHERE id = … AND copies_left &gt; 0</c>. PostgreSQL locks
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

        var decremented = await db.Editions
            .Where(e => e.Id == editionId && e.CopiesLeft > 0)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.CopiesLeft, e => e.CopiesLeft - 1), ct);
        if (decremented == 0)
        {
            return await db.Editions.AnyAsync(e => e.Id == editionId, ct)
                ? new ReserveOutcome.SoldOut()
                : new ReserveOutcome.NotFound();
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
