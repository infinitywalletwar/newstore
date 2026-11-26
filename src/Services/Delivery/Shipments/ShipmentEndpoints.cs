using System.Security.Claims;

using Microsoft.Extensions.Options;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Delivery.Shipments;

public static class ShipmentEndpoints
{
    public static IEndpointRouteBuilder MapShipmentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/me/shipments", ListAsync).RequireAuthorization(Policies.SignedIn);
        routes.MapGet("/shipments/{id:guid}", GetAsync).RequireAuthorization(Policies.SignedIn);

        // The map's starting point. Public: the checkout page shows it before the buyer places the pin, and it holds nobody's data.
        routes.MapGet("/delivery/warehouse", (IOptions<WarehouseOptions> options) =>
                new WarehouseResponse(options.Value.Name, options.Value.Latitude, options.Value.Longitude))
            .AllowAnonymous();
        return routes;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, ShipmentReader reader, CancellationToken ct) =>
        Results.Ok(await reader.ListAsync(user.UserId(), ct));

    /// <summary>
    /// Someone else's shipment is a 404, the same answer as a missing id, not a 403. A shipment holds the buyer's address, so it is a
    /// secret resource, and RFC 9110 lets a server hide a forbidden resource behind a 404. ADR 0016 makes the same argument the other
    /// way round: it answers 403 for someone else's edition only because an edition is public, so a 404 would hide nothing.
    /// </summary>
    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, ShipmentReader reader, CancellationToken ct) =>
        await reader.GetAsync(id, user.UserId(), ct) is { } shipment ? Results.Ok(shipment) : Problems.NotFound("Shipment");
}
