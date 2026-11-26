using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;

namespace NewStore.Delivery.Courier;

/// <summary>The courier's position number <see cref="Seq"/> on a trip: what it reports for that tick.</summary>
public sealed record TimelinePoint(int Seq, ShipmentStatus Status, GeoPoint Position, DateTimeOffset RecordedAt)
{
    /// <summary>The message on <see cref="CourierPosition.Topic"/> for this point of <paramref name="shipmentId"/>'s trip.</summary>
    public CourierPosition ToPosition(Guid shipmentId) => new()
    {
        ShipmentId = shipmentId,
        Seq = Seq,
        Status = Status.ToString(),
        Latitude = Position.Latitude,
        Longitude = Position.Longitude,
        RecordedAt = RecordedAt,
    };
}

/// <summary>
/// A trip as a pure function of its start (<c>courier_started_at</c>) and <see cref="CourierOptions"/>: point <c>n</c> is at
/// <c>t = n × TickInterval</c> after the start. Any courier that knows the start computes the same point for the same seq, so a
/// courier that takes over a trip carries it on from where the clock says, and a repeated seq is an identical message.
/// <list type="bullet">
/// <item><c>t &lt; PrepareTime</c>: <see cref="ShipmentStatus.Preparing"/>, at the warehouse.</item>
/// <item>The first seq at or after <c>PrepareTime</c>: <see cref="ShipmentStatus.PickedUp"/>.</item>
/// <item><see cref="LastSeq"/>: <see cref="ShipmentStatus.Delivered"/>, exactly at the pin (it wins over PickedUp).</item>
/// <item>Every other seq: <see cref="ShipmentStatus.InTransit"/>.</item>
/// </list>
/// From the pickup on, the position is <c>from + f × (to − from)</c> with <c>f = clamp((t − PrepareTime) / TravelTime, 0, 1)</c>.
/// </summary>
public static class CourierTimeline
{
    /// <summary><c>ceil((PrepareTime + TravelTime) / TickInterval)</c>: the first tick at or after the trip's end.</summary>
    public static int LastSeq(CourierOptions options) => CeilTicks(options.PrepareTime + options.TravelTime, options);

    /// <summary>The seq of the last whole tick by <paramref name="now"/>, between 0 and <see cref="LastSeq"/>.</summary>
    public static int SeqAt(DateTimeOffset now, DateTimeOffset startedAt, CourierOptions options)
    {
        var elapsed = now - startedAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        // Whole ticks, in integer ticks of 100 ns, so no floating-point rounding moves a point to a neighbouring seq.
        return (int)Math.Min(elapsed.Ticks / options.TickInterval.Ticks, LastSeq(options));
    }

    public static TimelinePoint At(int seq, DateTimeOffset startedAt, GeoPoint from, GeoPoint to, CourierOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seq);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seq, LastSeq(options));

        var t = seq * options.TickInterval;
        var recordedAt = startedAt + t;
        if (seq == LastSeq(options))
        {
            return new TimelinePoint(seq, ShipmentStatus.Delivered, to, recordedAt);
        }

        if (t < options.PrepareTime)
        {
            return new TimelinePoint(seq, ShipmentStatus.Preparing, from, recordedAt);
        }

        var status = seq == CeilTicks(options.PrepareTime, options) ? ShipmentStatus.PickedUp : ShipmentStatus.InTransit;
        var f = Math.Clamp((t - options.PrepareTime) / options.TravelTime, 0, 1);
        var position = new GeoPoint(
            from.Latitude + (f * (to.Latitude - from.Latitude)),
            from.Longitude + (f * (to.Longitude - from.Longitude)));
        return new TimelinePoint(seq, status, position, recordedAt);
    }

    /// <summary><c>ceil(duration / TickInterval)</c> in whole ticks.</summary>
    private static int CeilTicks(TimeSpan duration, CourierOptions options)
    {
        var tick = options.TickInterval.Ticks;
        return (int)((duration.Ticks + tick - 1) / tick);
    }
}
