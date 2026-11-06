using Microsoft.EntityFrameworkCore;

namespace NewStore.Inventory;

public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/editions/{editionId:guid}/reservations", ReserveAsync);
        routes.MapGet("/reservations/{id:guid}", GetAsync);
        routes.MapPost("/reservations/{id:guid}/confirm", ConfirmAsync);
        routes.MapPost("/reservations/{id:guid}/release", ReleaseAsync);
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

    private static async Task<IResult> ConfirmAsync(Guid id, ReservationService service, CancellationToken ct) =>
        await service.ConfirmAsync(id, ct) switch
        {
            ConfirmOutcome.Confirmed c => Results.Ok(ReservationResponse.From(c.Reservation, c.CopyNumber)),
            ConfirmOutcome.Expired => Problems.Conflict("reservation-expired", "The reservation ran out of time. The copy went back on sale."),
            ConfirmOutcome.Released => Problems.Conflict("reservation-released", "The reservation was released."),
            _ => Problems.NotFound("Reservation"),
        };

    private static async Task<IResult> ReleaseAsync(Guid id, ReservationService service, CancellationToken ct) =>
        await service.ReleaseAsync(id, ct) switch
        {
            ReleaseOutcome.Done => Results.NoContent(),
            ReleaseOutcome.AlreadyConfirmed => Problems.Conflict("reservation-confirmed", "A confirmed reservation cannot be released."),
            _ => Problems.NotFound("Reservation"),
        };
}
