using Microsoft.Extensions.Options;

namespace NewStore.Delivery.Shipments;

public static class ShipmentEndpoints
{
    public static IEndpointRouteBuilder MapShipmentEndpoints(this IEndpointRouteBuilder routes)
    {
        // The map's starting point. Public: the warehouse's address is on the website anyway, and the checkout page shows it before
        // the buyer signs in.
        routes.MapGet("/delivery/warehouse", (IOptions<WarehouseOptions> options) =>
                new WarehouseResponse(options.Value.Name, options.Value.Latitude, options.Value.Longitude))
            .AllowAnonymous();
        return routes;
    }
}
