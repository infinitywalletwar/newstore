using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.Delivery.Shipments;

namespace NewStore.Delivery.Courier;

/// <summary>A trip a courier now holds: its start (the timeline's zero), the warehouse and the buyer's pin.</summary>
public sealed record ClaimedShipment(Guid Id, DateTimeOffset StartedAt, GeoPoint From, GeoPoint To);

/// <summary>
/// The couriers' leases on <c>shipments</c> (ADR 0020's pattern, as Media's <c>JobQueue</c>): a claim takes an open shipment with
/// <c>courier_locked_by</c> and <c>courier_locked_until</c>, and every later change is guarded by <c>courier_locked_by = courier</c>,
/// so a courier whose lease another courier has taken changes nothing. Every "now" comes from <see cref="TimeProvider"/> as a
/// parameter, never <c>now()</c>, so tests move time. Scoped, like the <see cref="DeliveryDbContext"/> it uses.
/// </summary>
public sealed class CourierLeases(DeliveryDbContext db, TimeProvider time, IOptions<CourierOptions> options)
{
    /// <summary>
    /// Takes the oldest open shipment that nobody holds: one never claimed, one whose courier stopped renewing, or one a stopping
    /// courier released. The first claim sets <c>courier_started_at</c>; a takeover keeps it, so the trip carries on from where its
    /// clock says. Null when there is none.
    /// </summary>
    public async Task<ClaimedShipment?> ClaimAsync(string courierId, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        var lockedUntil = now + options.Value.Lease;

        // One statement. The subquery's FOR UPDATE locks the row it picks until the statement commits, and SKIP LOCKED makes a
        // second courier's subquery pass over that row instead of waiting for it (PostgreSQL, SELECT, "The Locking Clause"), so two
        // claims never return one shipment. Nothing composes LINQ on it: an UPDATE can't run as a subquery.
        var claimed = await db.Database
            .SqlQuery<ClaimRow>($"""
                UPDATE shipments SET courier_locked_by = {courierId}, courier_locked_until = {lockedUntil},
                       courier_started_at = COALESCE(courier_started_at, {now})
                WHERE id = (SELECT id FROM shipments
                            WHERE courier_finished_at IS NULL AND (courier_locked_until IS NULL OR courier_locked_until < {now})
                            ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
                RETURNING id, courier_started_at, warehouse_latitude, warehouse_longitude, latitude, longitude
                """)
            .ToListAsync(ct);

        return claimed.SingleOrDefault() is { } row
            ? new ClaimedShipment(
                row.Id,
                row.StartedAt,
                new GeoPoint(row.WarehouseLatitude, row.WarehouseLongitude),
                new GeoPoint(row.Latitude, row.Longitude))
            : null;
    }

    /// <summary>
    /// Moves the lease on for each of <paramref name="shipmentIds"/> this courier still holds and returns those. One that is missing
    /// was taken over by another courier after this one's lease ran out, or finished; the caller drops it.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RenewAsync(string courierId, IReadOnlyCollection<Guid> shipmentIds, CancellationToken ct)
    {
        if (shipmentIds.Count == 0)
        {
            return [];
        }

        var now = time.GetUtcNowToMicrosecond();
        var ids = shipmentIds.ToArray();

        // A scalar SqlQuery that nothing composes on runs as written, so the RETURNING column needs no "Value" alias.
        return await db.Database
            .SqlQuery<Guid>($"""
                UPDATE shipments SET courier_locked_until = {now + options.Value.Lease}
                WHERE id = ANY({ids}) AND courier_locked_by = {courierId} AND courier_finished_at IS NULL
                RETURNING id
                """)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The trip is over: <c>courier_finished_at</c> is set and the lease cleared. Call it only after Kafka acknowledged the last
    /// position. False when the shipment is no longer this courier's.
    /// </summary>
    public async Task<bool> FinishAsync(Guid shipmentId, string courierId, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        var finished = await Held(courierId)
            .Where(s => s.Id == shipmentId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.CourierFinishedAt, now)
                    .SetProperty(s => s.CourierLockedBy, (string?)null)
                    .SetProperty(s => s.CourierLockedUntil, (DateTimeOffset?)null),
                ct);
        return finished == 1;
    }

    /// <summary>
    /// The courier is stopping: each of its open shipments' leases ends now, so another courier claims it on its next tick instead
    /// of waiting for the lease to run out. <c>courier_locked_by</c> stays, as a record of who drove it last. Returns how many
    /// shipments were released.
    /// </summary>
    public async Task<int> ReleaseAsync(string courierId, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();
        return await Held(courierId).ExecuteUpdateAsync(set => set.SetProperty(s => s.CourierLockedUntil, now), ct);
    }

    /// <summary>The open shipments this courier holds.</summary>
    private IQueryable<Shipment> Held(string courierId) =>
        db.Shipments.Where(s => s.CourierLockedBy == courierId && s.CourierFinishedAt == null);

    /// <summary>The claim's RETURNING row. The column names are explicit, so the mapping doesn't depend on a naming convention.</summary>
    private sealed class ClaimRow
    {
        [Column("id")]
        public Guid Id { get; init; }

        [Column("courier_started_at")]
        public DateTimeOffset StartedAt { get; init; }

        [Column("warehouse_latitude")]
        public double WarehouseLatitude { get; init; }

        [Column("warehouse_longitude")]
        public double WarehouseLongitude { get; init; }

        [Column("latitude")]
        public double Latitude { get; init; }

        [Column("longitude")]
        public double Longitude { get; init; }
    }
}
