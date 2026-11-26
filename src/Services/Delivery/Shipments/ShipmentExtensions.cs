namespace NewStore.Delivery.Shipments;

public static class ShipmentExtensions
{
    /// <summary><see cref="ShipmentReader"/>, which the REST API (and, from Task 5, the tracking hub) reads through.</summary>
    public static IHostApplicationBuilder AddShipments(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<ShipmentReader>();
        return builder;
    }
}
