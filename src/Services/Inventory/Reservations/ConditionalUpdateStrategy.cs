using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

namespace NewStore.Inventory;

/// <summary>
/// One statement takes the copy and records the reservation, with no explicit transaction around it:
/// <c>WITH taken AS (UPDATE editions SET copies_left = copies_left - 1, locked_at = COALESCE(locked_at, @now)
/// WHERE id = … AND copies_left &gt; 0 AND ended_at IS NULL RETURNING id) INSERT INTO reservations … SELECT … FROM taken</c>.
/// PostgreSQL locks the edition row for the update, so concurrent reserves of one edition queue on the row, and each one
/// sees the count the one before it left. The statement is its own transaction, so the decrement and the reservation
/// commit together or not at all, and the row lock is held for one statement, not for several round trips. There is
/// no retry loop to tune; the one resend, on a deadlock, is in <see cref="InsertTakenAsync"/>. ADR 0013, "Amendment, 2026-11", has the statement sequence and why each guarantee holds.
/// </summary>
public sealed class ConditionalUpdateStrategy(
    InventoryDbContext db, TimeProvider time, IOptions<InventoryOptions> options, InventoryMetrics metrics)
    : IReserveStrategy
{
    public async Task<ReserveOutcome> ReserveAsync(
        Guid reservationId, Guid editionId, Guid buyerId, CancellationToken cancellationToken)
    {
        var outcome = await TryReserveAsync(reservationId, editionId, buyerId, cancellationToken);
        metrics.RecordReserve(outcome, attempts: 1);
        return outcome;
    }

    private async Task<ReserveOutcome> TryReserveAsync(Guid reservationId, Guid editionId, Guid buyerId, CancellationToken ct)
    {
        var reservation = ReserveSupport.NewPending(reservationId, editionId, buyerId, time, options.Value);
        int inserted;
        try
        {
            inserted = await InsertTakenAsync(reservation, ct);
        }
        catch (PostgresException ex) when (ReserveSupport.IsReservationConflict(ex))
        {
            // The whole statement failed, and its decrement with it. The lookup is a new statement, so it sees the reservation
            // that broke the constraint: one under this id (a retry, or the id is taken), or none (the buyer holds another).
            return await ReserveSupport.ResolveConflictAsync(db, reservationId, editionId, buyerId, ct);
        }

        return inserted == 1
            ? new ReserveOutcome.Reserved(reservation)
            : await WhyNoCopyAsync(reservationId, editionId, buyerId, ct);
    }

    /// <summary>
    /// Sends the reserve statement; on a deadlock (SQLSTATE 40P01) sends it once more. The statement is its own transaction,
    /// so PostgreSQL rolled all of it back, and the caller's id makes the resend safe. A second 40P01 propagates. ADR 0013,
    /// "Amendment, 2026-11", names the two cycles this covers (the sweep expiring the buyer's reservation while they reserve
    /// the edition again, with a new id or the same one).
    /// </summary>
    private async Task<int> InsertTakenAsync(Reservation reservation, CancellationToken ct)
    {
        try
        {
            return await SendReserveStatementAsync(reservation, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            return await SendReserveStatementAsync(reservation, ct);
        }
    }

    private async Task<int> SendReserveStatementAsync(Reservation reservation, CancellationToken ct) =>
        // The INSERT reads only the UPDATE's RETURNING, so it inserts a row only when a copy was taken. The first
        // reserve sets locked_at; later ones keep it.
        await db.Database.ExecuteSqlAsync($"""
            WITH taken AS (
                UPDATE editions SET copies_left = copies_left - 1, locked_at = COALESCE(locked_at, {reservation.ReservedAt})
                WHERE id = {reservation.EditionId} AND copies_left > 0 AND ended_at IS NULL
                RETURNING id)
            INSERT INTO reservations (id, edition_id, buyer_id, status, reserved_at, expires_at)
            SELECT {reservation.Id}, id, {reservation.BuyerId}, {reservation.Status.ToString()}, {reservation.ReservedAt}, {reservation.ExpiresAt}
            FROM taken
            """, ct);

    /// <summary>
    /// The statement took no copy: sold out, sale ended or no such edition, or a request with this id took the last copy
    /// while ours waited on the edition row. A new statement starts with a new snapshot under Read Committed, so this read
    /// sees that request's reservation. It reads the edition and the id together; an unknown edition costs one more read.
    /// </summary>
    private async Task<ReserveOutcome> WhyNoCopyAsync(Guid reservationId, Guid editionId, Guid buyerId, CancellationToken ct)
    {
        var found = await db.Editions.AsNoTracking()
            .Where(e => e.Id == editionId)
            .Select(e => new { e.EndedAt, Existing = db.Reservations.FirstOrDefault(r => r.Id == reservationId) })
            .FirstOrDefaultAsync(ct);
        return found switch
        {
            null => await ReserveSupport.ExistingOrAsync(db, reservationId, editionId, buyerId, new ReserveOutcome.NotFound(), ct),
            { Existing: { } existing } => ReserveSupport.ForKnownId(existing, editionId, buyerId),
            { EndedAt: not null } => new ReserveOutcome.SaleEnded(),
            _ => new ReserveOutcome.SoldOut(),
        };
    }
}
