using NewStore.Delivery.Courier;
using NewStore.Delivery.Shipments;

namespace NewStore.Delivery.Tests;

/// <summary>
/// The timeline is a pure function of the trip's start, so these tests need no host. They use the defaults (prepare 30 s, travel
/// 270 s, a tick every 2 s) and a trip from (0, 0) to (1, 2), so a point's latitude is the fraction of the way and its longitude twice
/// that.
/// </summary>
public sealed class CourierTimelineTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint From = new(0, 0);
    private static readonly GeoPoint To = new(1, 2);

    private static TimelinePoint At(int seq, CourierOptions? options = null) =>
        CourierTimeline.At(seq, StartedAt, From, To, options ?? new CourierOptions());

    [Fact]
    public void Before_the_prepare_time_the_courier_is_at_the_warehouse_preparing()
    {
        foreach (var seq in new[] { 0, 14 })
        {
            var point = At(seq);
            Assert.Equal(seq, point.Seq);
            Assert.Equal(ShipmentStatus.Preparing, point.Status);
            Assert.Equal(From, point.Position);
        }
    }

    [Fact]
    public void Pickup_is_the_first_tick_after_the_prepare_time()
    {
        var point = At(15);

        Assert.Equal(ShipmentStatus.PickedUp, point.Status);
        Assert.Equal(From, point.Position);
        Assert.Equal(ShipmentStatus.InTransit, At(16).Status);
    }

    [Fact]
    public void In_transit_moves_along_the_straight_line()
    {
        // Seq 82 is 164 s into the trip: 134 s of the 270 s on the road.
        var point = At(82);
        var f = 134.0 / 270;

        Assert.Equal(ShipmentStatus.InTransit, point.Status);
        Assert.Equal(f * 1, point.Position.Latitude);
        Assert.Equal(f * 2, point.Position.Longitude);
    }

    [Fact]
    public void Last_tick_is_delivered_at_the_pin()
    {
        var options = new CourierOptions();

        Assert.Equal(150, CourierTimeline.LastSeq(options));
        var point = At(150, options);
        Assert.Equal(ShipmentStatus.Delivered, point.Status);
        Assert.Equal(To, point.Position);
        Assert.Equal(ShipmentStatus.InTransit, At(149, options).Status);
    }

    [Fact]
    public void Seq_is_whole_ticks_clamped_to_the_last()
    {
        var options = new CourierOptions();

        Assert.Equal(1, CourierTimeline.SeqAt(StartedAt + TimeSpan.FromSeconds(3.9), StartedAt, options));
        Assert.Equal(150, CourierTimeline.SeqAt(StartedAt + TimeSpan.FromHours(1), StartedAt, options));
        Assert.Equal(0, CourierTimeline.SeqAt(StartedAt - TimeSpan.FromSeconds(5), StartedAt, options));
    }

    [Fact]
    public void Times_that_are_not_whole_ticks_still_end_at_the_pin()
    {
        // 5 s of preparing is 2.5 ticks and 30 s in all is 15: the pickup rounds up to seq 3, and the last seq is 15.
        var options = new CourierOptions { PrepareTime = TimeSpan.FromSeconds(5), TravelTime = TimeSpan.FromSeconds(25) };

        Assert.Equal(15, CourierTimeline.LastSeq(options));
        Assert.Equal(ShipmentStatus.Preparing, At(2, options).Status);
        Assert.Equal(ShipmentStatus.PickedUp, At(3, options).Status);
        Assert.Equal(ShipmentStatus.InTransit, At(14, options).Status);
        var last = At(15, options);
        Assert.Equal(ShipmentStatus.Delivered, last.Status);
        Assert.Equal(To, last.Position);
    }

    [Fact]
    public void Recorded_at_follows_the_seq_not_the_clock()
    {
        Assert.Equal(StartedAt + TimeSpan.FromSeconds(14), At(7).RecordedAt);
    }
}
