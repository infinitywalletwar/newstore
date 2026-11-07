using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Inventory;
using NewStore.Messaging;

namespace NewStore.Inventory;

/// <summary>
/// The snapshot pass (ADR 0011), on a static type so the publisher loop and the tests call the same code.
/// </summary>
public static class EditionSnapshots
{
    // One key per purpose in Inventory's database, ADR 0011. A second purpose takes the next number.
    private const long AdvisoryLockKey = 11_001;

    /// <summary>
    /// One pass, in one transaction: enqueues an <see cref="EditionSnapshot"/> for up to <paramref name="batchSize"/>
    /// editions whose state differs from their <c>edition_snapshots</c> row (or that have none) and records what it
    /// sent. Returns how many it enqueued, or null when another pass held the lock.
    /// </summary>
    public static async Task<int?> PublishAsync(
        InventoryDbContext db, IOutbox outbox, DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(outbox);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The lock goes at the end of the transaction, on commit or rollback, so a crashed pass cannot leave it held.
        var locked = await db.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey}) AS \"Value\"")
            .SingleAsync(ct);
        if (!locked)
        {
            return null;
        }

        // A plain read: no FOR UPDATE, so a reserve never waits for this pass. EF translates != on nullable
        // values with C# meaning (null equals null), which is the null-safe comparison of ended_at.
        var changed = await (
            from e in db.Editions.AsNoTracking()
            join s in db.EditionSnapshots.AsNoTracking() on e.Id equals s.EditionId into snapshots
            from s in snapshots.DefaultIfEmpty()
            where s == null
                || s.Price != e.Price
                || s.CopiesTotal != e.CopiesTotal
                || s.CopiesLeft != e.CopiesLeft
                || s.EndedAt != e.EndedAt
            orderby e.Id
            select new { Edition = e, PreviousVersion = s == null ? (long?)null : s.Version })
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var (edition, previousVersion) in changed.Select(c => (c.Edition, c.PreviousVersion)))
        {
            var version = (previousVersion ?? 0) + 1;

            outbox.Enqueue(
                new EditionSnapshot
                {
                    EventId = Guid.CreateVersion7(now),
                    OccurredAt = now,
                    EditionId = edition.Id,
                    VideoId = edition.VideoId,
                    Format = edition.Format.ToString(),
                    Price = edition.Price,
                    CopiesTotal = edition.CopiesTotal,
                    CopiesLeft = edition.CopiesLeft,
                    EndedAt = edition.EndedAt,
                    Version = version,
                },
                EditionSnapshot.Topic,
                edition.Id.ToString());

            var row = new EditionSnapshotRow
            {
                EditionId = edition.Id,
                Version = version,
                Price = edition.Price,
                CopiesTotal = edition.CopiesTotal,
                CopiesLeft = edition.CopiesLeft,
                EndedAt = edition.EndedAt,
                PublishedAt = now,
            };
            if (previousVersion is null)
            {
                db.EditionSnapshots.Add(row);
            }
            else
            {
                db.EditionSnapshots.Update(row);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return changed.Count;
    }
}
