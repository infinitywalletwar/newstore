using Microsoft.EntityFrameworkCore;

namespace NewStore.Inventory;

public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/editions/{editionId:guid}/reservations", ReserveAsync);
        routes.MapGet("/reservations/{id:guid}", GetAsync);
        return routes;
    }

    private static async Task<IResult> ReserveAsync(
        Guid editionId, ReserveRequest request, IReserveStrategy strategy, HttpContext http, CancellationToken ct)
    {
        if (request.BuyerId == Guid.Empty)
        {
            return Problems.Validation(new() { ["buyerId"] = ["buyerId is required."] });
        }

        switch (await strategy.ReserveAsync(editionId, request.BuyerId, ct))
        {
            case ReserveOutcome.Reserved reserved:
                return Results.Created($"/reservations/{reserved.Reservation.Id}", ReservationResponse.From(reserved.Reservation));
            case ReserveOutcome.SoldOut:
                return Problems.Conflict("sold-out", "Every copy of this edition is taken.");
            case ReserveOutcome.AlreadyHolding:
                return Problems.Conflict("already-holding", "This buyer already holds a copy of this edition.");
            case ReserveOutcome.Contended:
                http.Response.Headers.RetryAfter = "1";
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Too many buyers at once. Try again.",
                    type: "urn:newstore:problem:contended");
            default:
                return Problems.NotFound("Edition");
        }
    }

    private static async Task<IResult> GetAsync(Guid id, InventoryDbContext db, CancellationToken ct)
    {
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reservation is null)
        {
            return Problems.NotFound("Reservation");
        }

        var copyNumber = await db.Copies
            .Where(c => c.ReservationId == id)
            .Select(c => (int?)c.Number)
            .FirstOrDefaultAsync(ct);
        return Results.Ok(ReservationResponse.From(reservation, copyNumber));
    }
}
