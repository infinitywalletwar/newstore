using Microsoft.EntityFrameworkCore;

namespace NewStore.Inventory;

public abstract record ConfirmOutcome
{
    /// <summary>Confirmed now, or by an earlier call: the number is the same.</summary>
    public sealed record Confirmed(Reservation Reservation, int CopyNumber) : ConfirmOutcome;
    public sealed record NotFound : ConfirmOutcome;
    public sealed record Expired : ConfirmOutcome;
    public sealed record Released : ConfirmOutcome;
}

public abstract record ReleaseOutcome
{
    /// <summary>Released now, or earlier (releasing is idempotent).</summary>
    public sealed record Done : ReleaseOutcome;
    public sealed record NotFound : ReleaseOutcome;
    public sealed record AlreadyConfirmed : ReleaseOutcome;
}

/// <summary>Confirm, release and sweep: everything that happens to a reservation after it was made.</summary>
public sealed class ReservationService(InventoryDbContext db, TimeProvider time)
{
    public async Task<ConfirmOutcome> ConfirmAsync(Guid reservationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Two confirms of one reservation run one after the other: the second waits here for the first to commit.
        var reservation = await LockAsync(reservationId, ct);
        if (reservation is null)
        {
            return new ConfirmOutcome.NotFound();
        }

        switch (reservation.Status)
        {
            case ReservationStatus.Confirmed:
                var number = await db.Copies
                    .Where(c => c.ReservationId == reservationId)
                    .Select(c => c.Number)
                    .SingleAsync(ct);
                return new ConfirmOutcome.Confirmed(reservation, number);
            case ReservationStatus.Released:
                return new ConfirmOutcome.Released();
            case ReservationStatus.Expired:
                return new ConfirmOutcome.Expired();
        }

        var now = time.GetUtcNow();
        if (reservation.ExpiresAt <= now)
        {
            // Too late, and the sweeper has not got here yet: do its work for this one reservation.
            reservation.Status = ReservationStatus.Expired;
            await ReturnCopyAsync(reservation.EditionId, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new ConfirmOutcome.Expired();
        }

        // The number comes from a counter on the edition row, not from count(*) + 1. The UPDATE locks the
        // row until this transaction ends, so numbers follow commit order, and a rolled-back confirm
        // rolls the counter back too: no gaps. RETURNING makes it one statement. The query is not
        // composed with LINQ (no SingleAsync). When a LINQ operator follows, EF treats the SQL as a subquery
        // and composes over it ("Composing with LINQ" and "Querying scalar types" in
        // https://learn.microsoft.com/ef/core/querying/sql-queries). PostgreSQL allows a data-modifying
        // statement only at the top level or in a WITH attached to it
        // (https://www.postgresql.org/docs/current/queries-with.html#QUERIES-WITH-MODIFYING),
        // so an UPDATE inside a subquery is not valid. ToListAsync adds no operator; we take the single row ourselves.
        var assigned = await db.Database
            .SqlQuery<int>($"""
                UPDATE editions SET copies_assigned = copies_assigned + 1
                WHERE id = {reservation.EditionId}
                RETURNING copies_assigned AS "Value"
                """)
            .ToListAsync(ct);

        db.Copies.Add(new Copy
        {
            Id = Guid.CreateVersion7(),
            EditionId = reservation.EditionId,
            Number = assigned.Single(),
            OwnerId = reservation.BuyerId,
            ReservationId = reservation.Id,
            AssignedAt = now,
        });
        reservation.Status = ReservationStatus.Confirmed;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ConfirmOutcome.Confirmed(reservation, assigned.Single());
    }

    public async Task<ReleaseOutcome> ReleaseAsync(Guid reservationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var reservation = await LockAsync(reservationId, ct);
        if (reservation is null)
        {
            return new ReleaseOutcome.NotFound();
        }

        switch (reservation.Status)
        {
            case ReservationStatus.Confirmed:
                return new ReleaseOutcome.AlreadyConfirmed();
            case ReservationStatus.Pending:
                reservation.Status = ReservationStatus.Released;
                await ReturnCopyAsync(reservation.EditionId, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                break;
        }

        return new ReleaseOutcome.Done();
    }

    // Lock order, for every path that touches both tables: the reservation row first, the edition row second.
    // - ConfirmAsync and ReleaseAsync: LockAsync (SELECT ... FOR UPDATE on the reservation), then
    //   ReturnCopyAsync or the counter UPDATE on the edition.
    // - SweepAsync: the data-modifying WITH updates the reservations, and the outer UPDATE editions reads
    //   `expired` through a GROUP BY, planned as a HashAggregate. For that node the executor fills the hash
    //   table from all of `expired` before it retrieves the first group: ExecAgg calls agg_fill_hash_table, then
    //   agg_retrieve_hash_table (nodeAgg.c, REL_18_STABLE, https://github.com/postgres/postgres/blob/REL_18_STABLE/src/backend/executor/nodeAgg.c). So no edition row is locked
    //   before the last reservation row is. The plan is in ADR 0007, "Lock order".
    // PostgreSQL documents: "The sub-statements in WITH are executed concurrently with each other and with
    // the main query. Therefore, when using data-modifying statements in WITH, the order in which the
    // specified updates actually happen is unpredictable."
    // (https://www.postgresql.org/docs/current/queries-with.html#QUERIES-WITH-MODIFYING).
    // That sentence is why this comment exists. That is the executor's behaviour for the plan chosen, not a documented SQL guarantee;
    // LockOrderTests (50 expired confirms against 5 sweeps, 20 rounds) is the evidence, and shows no deadlock.
    /// <summary>
    /// Expires every Pending reservation due at <paramref name="now"/> and gives the copies back, in one
    /// statement: atomic, and safe with several instances sweeping (the second finds nothing left to update).
    /// Returns how many reservations expired.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        // RETURNING one row per edition lets us count; the plain UPDATE would only report editions touched.
        var perEdition = await db.Database
            .SqlQuery<int>($"""
                WITH expired AS (
                    UPDATE reservations SET status = 'Expired'
                    WHERE status = 'Pending' AND expires_at <= {now}
                    RETURNING edition_id)
                UPDATE editions e SET copies_left = e.copies_left + x.n
                FROM (SELECT edition_id, count(*) AS n FROM expired GROUP BY edition_id) x
                WHERE e.id = x.edition_id
                RETURNING x.n::int AS "Value"
                """)
            .ToListAsync(ct);
        return perEdition.Sum();
    }

    /// <summary>
    /// Loads the reservation and holds its row lock until the transaction ends. Not composed with LINQ,
    /// for the same reason as the counter query in <see cref="ConfirmAsync"/>.
    /// </summary>
    private async Task<Reservation?> LockAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.Reservations
            .FromSql($"SELECT * FROM reservations WHERE id = {id} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    // Atomic in SQL, so it never trips the edition's concurrency token or loses a concurrent reserve's decrement.
    private Task<int> ReturnCopyAsync(Guid editionId, CancellationToken ct) =>
        db.Editions
            .Where(e => e.Id == editionId)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.CopiesLeft, e => e.CopiesLeft + 1), ct);
}
