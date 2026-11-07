using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Inventory;
using NewStore.Messaging;

namespace NewStore.Catalog.Editions;

/// <summary>
/// Keeps the newest snapshot of each edition (ADR 0011). One upsert, guarded by the version: a snapshot that is
/// not newer than the row changes nothing, so a late or reordered message cannot move the copy back in time.
/// </summary>
public sealed partial class EditionSnapshotHandler(
    CatalogDbContext db, TimeProvider timeProvider, ILogger<EditionSnapshotHandler> logger)
    : IIntegrationEventHandler<EditionSnapshot>
{
    public async Task HandleAsync(EditionSnapshot integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var now = timeProvider.GetUtcNow();

        // Runs in the transaction the dispatcher opened on this context, so it commits with the inbox row.
        // The interpolated values become parameters, not text in the SQL.
        var changed = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO editions (id, video_id, format, price, copies_total, copies_left, ended_at, version, updated_at)
            VALUES ({integrationEvent.EditionId}, {integrationEvent.VideoId}, {integrationEvent.Format}, {integrationEvent.Price},
                    {integrationEvent.CopiesTotal}, {integrationEvent.CopiesLeft}, {integrationEvent.EndedAt}, {integrationEvent.Version}, {now})
            ON CONFLICT (id) DO UPDATE SET
                video_id = excluded.video_id,
                format = excluded.format,
                price = excluded.price,
                copies_total = excluded.copies_total,
                copies_left = excluded.copies_left,
                ended_at = excluded.ended_at,
                version = excluded.version,
                updated_at = excluded.updated_at
            WHERE editions.version < excluded.version
            """,
            cancellationToken);

        if (changed == 0)
        {
            StaleSnapshotIgnored(logger, integrationEvent.EditionId, integrationEvent.Version);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored a stale snapshot of edition {EditionId}: version {Version} is not newer than the stored one.")]
    private static partial void StaleSnapshotIgnored(ILogger logger, Guid editionId, long version);
}
