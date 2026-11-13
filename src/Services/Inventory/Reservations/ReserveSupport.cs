using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace NewStore.Inventory;

/// <summary>
/// What both reserve strategies share. The caller chooses the reservation id, so a reserve is safe to send again: the
/// id is looked up before a copy is taken (<see cref="FindByIdAsync"/>), and again on every way the reserve fails
/// (<see cref="ExistingOrAsync"/>, <see cref="ResolveConflictAsync"/>), because a parallel request with the same id may
/// have won in between (ADR 0013).
/// </summary>
internal static class ReserveSupport
{
    /// <summary>
    /// True when the insert broke a unique constraint of <c>reservations</c>: the primary key (the id was taken) or the
    /// partial unique index on (edition, buyer) (the buyer already holds a copy). A unique violation on another table is not ours.
    /// </summary>
    public static bool IsReservationConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "reservations" };

    public static Reservation NewPending(
        Guid reservationId, Guid editionId, Guid buyerId, TimeProvider time, InventoryOptions options)
    {
        var now = time.GetUtcNow();
        return new Reservation
        {
            Id = reservationId,
            EditionId = editionId,
            BuyerId = buyerId,
            Status = ReservationStatus.Pending,
            ReservedAt = now,
            ExpiresAt = now + options.ReservationTtl,
        };
    }

    /// <summary>
    /// Step 1, before a copy is taken. Null when nobody used the id; <see cref="ReserveOutcome.Existing"/> when it belongs
    /// to a reservation of this edition and buyer; <see cref="ReserveOutcome.IdTaken"/> when it belongs to another.
    /// </summary>
    public static async Task<ReserveOutcome?> FindByIdAsync(
        InventoryDbContext db, Guid reservationId, Guid editionId, Guid buyerId, CancellationToken ct)
    {
        var existing = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        return existing switch
        {
            null => null,
            _ when existing.EditionId == editionId && existing.BuyerId == buyerId => new ReserveOutcome.Existing(existing),
            _ => new ReserveOutcome.IdTaken(),
        };
    }

    /// <summary>
    /// Every way a reserve fails to take a copy (sold out, sale ended, no such edition) ends here, because the failure may
    /// be the result of this very id: a retry that overlapped the original waited for it, and then found the last copy
    /// gone. The lookup is a new statement, and under Read Committed each statement starts with a new snapshot of what
    /// has committed, so it sees the original's reservation. Found: answer as in step 1. Not found: <paramref name="failure"/>.
    /// </summary>
    public static async Task<ReserveOutcome> ExistingOrAsync(
        InventoryDbContext db, Guid reservationId, Guid editionId, Guid buyerId, ReserveOutcome failure, CancellationToken ct) =>
        await FindByIdAsync(db, reservationId, editionId, buyerId, ct) ?? failure;

    /// <summary>
    /// Step 2, after a unique violation and the rollback: the same lookup. A reservation now exists under the id, so the
    /// violation was the id's (the parallel request with this id won, or the id is taken). No reservation under the id
    /// means the violation was the (edition, buyer) index: the buyer already holds a copy.
    /// </summary>
    public static async Task<ReserveOutcome> ResolveConflictAsync(
        InventoryDbContext db, Guid reservationId, Guid editionId, Guid buyerId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await FindByIdAsync(db, reservationId, editionId, buyerId, ct) ?? new ReserveOutcome.AlreadyHolding();
    }
}
