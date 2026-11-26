using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NewStore.Delivery.Courier;
using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;

namespace NewStore.Delivery.Tests;

/// <summary>
/// Delivery with a short trip: prepare 4 s, travel 6 s, a tick every 2 s, so <c>LastSeq</c> is 5 and <c>PickedUp</c> is seq 2; the
/// lease is 9 s, renewed every 3 s. The role stays <c>Api</c>, so no <see cref="CourierService"/> runs: the tests make runners with
/// <see cref="NewRunner"/> and tick them by hand on the fake clock.
/// </summary>
public class CourierFixture(PostgresServer postgres, KafkaServer kafka) : DeliveryFixture(postgres, kafka)
{
    public CourierOptions Courier => Services.GetRequiredService<IOptions<CourierOptions>>().Value;

    protected override void ConfigureSettings(Dictionary<string, string?> settings)
    {
        settings["Delivery:Courier:PrepareTime"] = "00:00:04";
        settings["Delivery:Courier:TravelTime"] = "00:00:06";
        settings["Delivery:Courier:TickInterval"] = "00:00:02";
        settings["Delivery:Courier:Lease"] = "00:00:09";
        settings["Delivery:Courier:LeaseRenewInterval"] = "00:00:03";
    }

    /// <summary>
    /// A second courier in the same process, as a second container would be: its own id and its own held trips, the app's database
    /// and producer. <paramref name="claimsPerTick"/> overrides <see cref="CourierOptions.ClaimsPerTick"/> for this runner only.
    /// </summary>
    public CourierRunner NewRunner(int? claimsPerTick = null)
    {
        if (claimsPerTick is not { } claims)
        {
            return ActivatorUtilities.CreateInstance<CourierRunner>(Services);
        }

        // A fresh copy bound from the same section as the app's options, so every setting, including one added later, is the app's.
        var options = Services.GetRequiredService<IConfiguration>().GetSection(CourierOptions.SectionName).Get<CourierOptions>()
            ?? new CourierOptions();
        options.ClaimsPerTick = claims;
        return ActivatorUtilities.CreateInstance<CourierRunner>(Services, Options.Create(options));
    }
}

/// <summary>Role <c>Courier</c>: its <see cref="CourierService"/> ticks whenever the test moves the fake clock by a tick.</summary>
public sealed class RunningCourierFixture(PostgresServer postgres, KafkaServer kafka) : CourierFixture(postgres, kafka)
{
    protected override DeliveryRole Role => DeliveryRole.Courier;
}

public static class CourierTestData
{
    /// <summary>The shipment's row as it is now.</summary>
    public static Task<Shipment> RowAsync(this DeliveryFixture fixture, Guid id) =>
        fixture.QueryAsync(db => db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken));

    /// <summary>
    /// The message any courier must send for <paramref name="seq"/> of <paramref name="shipment"/>'s trip: the timeline's point,
    /// mapped the way the courier maps it.
    /// </summary>
    public static CourierPosition Expected(Shipment shipment, int seq, CourierOptions options) =>
        CourierTimeline.At(
                seq,
                shipment.CourierStartedAt!.Value,
                new GeoPoint(shipment.WarehouseLatitude, shipment.WarehouseLongitude),
                new GeoPoint(shipment.Latitude, shipment.Longitude),
                options)
            .ToPosition(shipment.Id);

    /// <summary>
    /// Marks every open shipment finished, so a test's claims see only the shipments it creates. A claim takes the oldest open
    /// shipment, and the tests of one class share a database.
    /// </summary>
    public static Task CloseOpenShipmentsAsync(this DeliveryFixture fixture)
    {
        var now = fixture.Clock.GetUtcNow();
        return fixture.QueryAsync(db => db.Shipments
            .Where(s => s.CourierFinishedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.CourierFinishedAt, now)
                    .SetProperty(s => s.CourierLockedBy, (string?)null)
                    .SetProperty(s => s.CourierLockedUntil, (DateTimeOffset?)null),
                TestContext.Current.CancellationToken));
    }
}
