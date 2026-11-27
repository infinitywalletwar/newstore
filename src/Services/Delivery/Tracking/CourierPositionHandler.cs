using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using NewStore.Delivery.Shipments;
using NewStore.Messaging;

namespace NewStore.Delivery.Tracking;

/// <summary>
/// Stores one batch of the courier's positions and sends the new ones to the browsers that track them. The stream has no inbox
/// (ADR 0022), so the batch is idempotent by its natural key: a position is stored once per (shipment, seq), and a shipment moves
/// only to a higher seq. A batch read twice, or seqs repeated by a courier that took a shipment over, store nothing and send nothing.
/// </summary>
public sealed partial class CourierPositionHandler(DeliveryDbContext db, IHubContext<TrackingHub> hub, ILogger<CourierPositionHandler> logger)
    : IStreamHandler<CourierPosition>
{
    public async Task HandleAsync(IReadOnlyList<CourierPosition> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var fresh = await StoreAsync(batch, cancellationToken);

        // After the commit, so a browser never sees a position the database doesn't hold.
        foreach (var position in fresh.OrderBy(p => p.ShipmentId).ThenBy(p => p.Seq))
        {
            try
            {
                await hub.Clients.Group(TrackingHub.Group(position.ShipmentId))
                    .SendAsync(TrackingHub.PositionMethod, position, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row is stored; the next Track's snapshot gives it to the browser. Throwing would retry a batch that is
                // already stored, and the retry would send nothing, because nothing in it is new any more.
                LogSendFailed(logger, ex, position.ShipmentId, position.Seq);
            }
        }
    }

    /// <summary>Stores the batch in one transaction and returns the positions that were not stored before.</summary>
    private async Task<List<CourierPosition>> StoreAsync(IReadOnlyList<CourierPosition> batch, CancellationToken ct)
    {
        // A batch may hold one position twice (a replayed partition); the first copy stands for both.
        var positions = batch.DistinctBy(p => (p.ShipmentId, p.Seq)).ToList();

        // A status that is not a ShipmentStatus name could never be read back, so the position is dropped like an unknown shipment's.
        var unreadable = positions.Where(p => p.Status is null || !Enum.IsDefined(typeof(ShipmentStatus), p.Status)).ToList();
        if (unreadable.Count > 0)
        {
            LogUnknownStatus(logger, unreadable.Count, string.Join(", ", unreadable.Select(p => p.Status).Distinct()));
            positions = [.. positions.Except(unreadable)];
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Positions of a shipment this database doesn't have (a topic replayed after a database reset) can never be applied, and
        // throwing would retry the batch for ever. They are dropped and logged once per batch (Review Focus 4).
        var ids = positions.Select(p => p.ShipmentId).Distinct().ToArray();
        var known = (await db.Database
            .SqlQuery<Guid>($"""SELECT id AS "Value" FROM shipments WHERE id = ANY({ids})""")
            .ToListAsync(ct)).ToHashSet();
        var unknown = positions.Where(p => !known.Contains(p.ShipmentId)).ToList();
        if (unknown.Count > 0)
        {
            LogUnknownShipments(logger, unknown.Count, string.Join(", ", unknown.Select(p => p.ShipmentId).Distinct()));
            positions = [.. positions.Where(p => known.Contains(p.ShipmentId))];
        }

        if (positions.Count == 0)
        {
            return [];
        }

        // One statement for the whole batch. ON CONFLICT DO NOTHING skips a (shipment_id, seq) already stored, and RETURNING
        // names only the rows this statement inserted: those are the positions no browser has seen yet. The snake_case naming
        // convention maps StoredKey's properties to these column names too.
        var shipmentIds = positions.Select(p => p.ShipmentId).ToArray();
        var seqs = positions.Select(p => p.Seq).ToArray();
        var latitudes = positions.Select(p => p.Latitude).ToArray();
        var longitudes = positions.Select(p => p.Longitude).ToArray();
        var statuses = positions.Select(p => p.Status).ToArray();
        var recordedAts = positions.Select(p => p.RecordedAt.ToUniversalTime()).ToArray();
        var inserted = await db.Database
            .SqlQuery<StoredKey>($"""
                INSERT INTO shipment_positions (shipment_id, seq, latitude, longitude, status, recorded_at)
                SELECT * FROM unnest({shipmentIds}, {seqs}, {latitudes}, {longitudes}, {statuses}, {recordedAts})
                ON CONFLICT DO NOTHING
                RETURNING shipment_id, seq
                """)
            .ToListAsync(ct);

        // Each shipment moves to its highest seq in the batch, and only forward: WHERE last_seq < seq ignores a stale or repeated
        // one. Pickup and delivery times are set once, from the stored path (which the INSERT above has just extended): the first
        // position past Preparing and the first Delivered one. Not from the batch's newest point, which in a catch-up batch can be
        // several ticks later than the one that reached the status.
        foreach (var newest in positions.GroupBy(p => p.ShipmentId).Select(g => g.MaxBy(p => p.Seq)!))
        {
            var at = newest.RecordedAt.ToUniversalTime();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE shipments SET
                    status = {newest.Status}, last_seq = {newest.Seq},
                    last_latitude = {newest.Latitude}, last_longitude = {newest.Longitude}, last_position_at = {at},
                    picked_up_at = COALESCE(picked_up_at, (SELECT min(recorded_at) FROM shipment_positions
                        WHERE shipment_id = {newest.ShipmentId} AND status <> 'Preparing')),
                    delivered_at = COALESCE(delivered_at, (SELECT min(recorded_at) FROM shipment_positions
                        WHERE shipment_id = {newest.ShipmentId} AND status = 'Delivered'))
                WHERE id = {newest.ShipmentId} AND last_seq < {newest.Seq}
                """, ct);
        }

        await transaction.CommitAsync(ct);

        var fresh = inserted.ToHashSet();
        return [.. positions.Where(p => fresh.Contains(new StoredKey(p.ShipmentId, p.Seq)))];
    }

    private sealed record StoredKey(Guid ShipmentId, int Seq);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} positions of shipments this database doesn't have: {ShipmentIds}.")]
    private static partial void LogUnknownShipments(ILogger logger, int count, string shipmentIds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} positions with a status that is not a shipment status: {Statuses}.")]
    private static partial void LogUnknownStatus(ILogger logger, int count, string statuses);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Position {Seq} of shipment {ShipmentId} is stored but could not be sent; the next snapshot has it.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, Guid shipmentId, int seq);
}
