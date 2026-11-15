using Microsoft.EntityFrameworkCore;

using NewStore.Contracts.Inventory;
using NewStore.Messaging;

namespace NewStore.Media;

/// <summary>
/// Records who owns a digital copy when Inventory says a copy was assigned. A <c>Dvd</c> changes nothing here. It runs in the inbox
/// transaction, so the row commits with the inbox row and a repeated message id never reaches it. <c>ON CONFLICT DO NOTHING</c> covers
/// the other repeat: a second copy of the same video for the same owner has a new message id and must not fail the message.
/// </summary>
public sealed class CopyAssignedHandler(MediaDbContext db, TimeProvider time) : IIntegrationEventHandler<CopyAssigned>
{
    public async Task HandleAsync(CopyAssigned integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        if (integrationEvent.Format != "Digital")
        {
            return;
        }

        var grantedAt = time.GetUtcNowToMicrosecond();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO digital_copies (owner_id, video_id, edition_id, copy_number, granted_at)
            VALUES ({integrationEvent.OwnerId}, {integrationEvent.VideoId}, {integrationEvent.EditionId}, {integrationEvent.CopyNumber}, {grantedAt})
            ON CONFLICT (owner_id, video_id) DO NOTHING
            """,
            cancellationToken);
    }
}
