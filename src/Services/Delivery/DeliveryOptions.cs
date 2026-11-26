namespace NewStore.Delivery;

/// <summary>
/// Which parts of Delivery this process runs. <c>Api</c> consumes Orders' event and serves the REST API; <c>Courier</c> drives the
/// trips. <c>All</c> runs both in one process (<c>dotnet run</c>, tests).
/// </summary>
public enum DeliveryRole { All, Api, Courier }

/// <summary>Bound from <c>Delivery:Warehouse</c>: where every trip starts. A shipment copies it when it is created.</summary>
public sealed class WarehouseOptions
{
    public const string SectionName = "Delivery:Warehouse";

    public string Name { get; set; } = "newstore warehouse, Tempelhof, Berlin";

    /// <summary>The former Tempelhof airport building.</summary>
    public double Latitude { get; set; } = 52.4836;

    public double Longitude { get; set; } = 13.3897;
}

/// <summary>Bound from <c>Delivery:Courier</c>: the simulated courier's timeline and its lease on a shipment.</summary>
public sealed class CourierOptions
{
    public const string SectionName = "Delivery:Courier";

    /// <summary>How long the parcel waits at the warehouse before it is picked up.</summary>
    public TimeSpan PrepareTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the trip from the warehouse to the buyer takes.</summary>
    public TimeSpan TravelTime { get; set; } = TimeSpan.FromSeconds(270);

    /// <summary>The time between two positions.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a courier's claim on a shipment lasts without a renewal.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most shipments one courier claims per tick, so two couriers split the work.</summary>
    public int ClaimsPerTick { get; set; } = 5;

    /// <summary>The most shipments one courier container drives at once.</summary>
    public int MaxShipments { get; set; } = 200;
}

/// <summary>Bound from <c>Delivery:Tracking</c>.</summary>
public sealed class TrackingOptions
{
    public const string SectionName = "Delivery:Tracking";

    /// <summary>Whether this API instance consumes the positions stream. False only for a test host that must not.</summary>
    public bool ConsumePositions { get; set; } = true;
}

public static class DeliveryOptionsExtensions
{
    /// <summary>
    /// Binds <c>Delivery:Warehouse</c>, <c>Delivery:Courier</c> and <c>Delivery:Tracking</c> in every role, checks them when the host
    /// starts, and returns <c>Delivery:Role</c> (default <see cref="DeliveryRole.All"/>). A bad value fails the start with a message that
    /// names the setting.
    /// </summary>
    public static DeliveryRole AddDeliveryOptions(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<WarehouseOptions>()
            .BindConfiguration(WarehouseOptions.SectionName)
            .Validate(o => o.Name is { Length: >= 1 and <= 100 }, "Delivery:Warehouse:Name must have 1 to 100 characters.")
            .Validate(o => o.Latitude is >= -90 and <= 90, "Delivery:Warehouse:Latitude must be between -90 and 90.")
            .Validate(o => o.Longitude is >= -180 and <= 180, "Delivery:Warehouse:Longitude must be between -180 and 180.")
            .ValidateOnStart();

        builder.Services.AddOptions<CourierOptions>()
            .BindConfiguration(CourierOptions.SectionName)
            .Validate(o => o.PrepareTime >= TimeSpan.Zero, "Delivery:Courier:PrepareTime must not be negative.")
            .Validate(o => o.TravelTime > TimeSpan.Zero, "Delivery:Courier:TravelTime must be positive.")
            .Validate(o => o.TickInterval > TimeSpan.Zero, "Delivery:Courier:TickInterval must be positive.")
            .Validate(o => o.LeaseRenewInterval > TimeSpan.Zero, "Delivery:Courier:LeaseRenewInterval must be positive.")
            // Three renewals fit in one lease, so one late or failed renewal doesn't lose the shipment to another courier.
            .Validate(o => o.Lease >= 3 * o.LeaseRenewInterval, "Delivery:Courier:Lease must be at least 3 times Delivery:Courier:LeaseRenewInterval.")
            .Validate(o => o.ClaimsPerTick >= 1, "Delivery:Courier:ClaimsPerTick must be at least 1.")
            .Validate(o => o.MaxShipments >= 1, "Delivery:Courier:MaxShipments must be at least 1.")
            .ValidateOnStart();

        builder.Services.AddOptions<TrackingOptions>()
            .BindConfiguration(TrackingOptions.SectionName)
            .ValidateOnStart();

        return builder.Configuration.GetValue("Delivery:Role", DeliveryRole.All);
    }
}
