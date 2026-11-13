using Microsoft.EntityFrameworkCore;

using NewStore.ServiceDefaults.Security;

namespace NewStore.Inventory;

public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder routes)
    {
        // Only Orders reserves, holds, confirms and releases: azp must be "orders". The edition endpoints stay open for now.
        var group = routes.MapGroup("").RequireAuthorization(Policies.Client("orders"));
        group.MapPost("/editions/{editionId:guid}/reservations", ReserveAsync);
        group.MapGet("/reservations/{id:guid}", GetAsync);
        group.MapPost("/reservations/{id:guid}/hold", HoldAsync);
        group.MapPost("/reservations/{id:guid}/confirm", ConfirmAsync);
        group.MapPost("/reservations/{id:guid}/release", ReleaseAsync);
        return routes;
    }

    private static async Task<IResult> ReserveAsync(
        Guid editionId, ReserveRequest request, IReserveStrategy strategy, InventoryDbContext db, HttpContext http,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.ReservationId == Guid.Empty)
        {
            errors["reservationId"] = ["reservationId is required."];
        }

        if (request.BuyerId == Guid.Empty)
        {
            errors["buyerId"] = ["buyerId is required."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        switch (await strategy.ReserveAsync(request.ReservationId, editionId, request.BuyerId, ct))
        {
            case ReserveOutcome.Reserved reserved:
                return Results.Created(
                    $"/reservations/{reserved.Reservation.Id}", await ToResponseAsync(db, reserved.Reservation, null, ct));
            case ReserveOutcome.Existing existing:
                return Results.Ok(await ToResponseAsync(db, existing.Reservation, await CopyNumberAsync(db, existing.Reservation.Id, ct), ct));
            case ReserveOutcome.IdTaken:
                return Problems.Conflict("reservation-id-taken", "This reservation id belongs to a reservation of another edition or buyer.");
            case ReserveOutcome.SoldOut:
                return Problems.Conflict("sold-out", "Every copy of this edition is taken.");
            case ReserveOutcome.SaleEnded:
                return Problems.Conflict("sale-ended", "This edition's sale has ended.");
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

        return Results.Ok(await ToResponseAsync(db, reservation, await CopyNumberAsync(db, id, ct), ct));
    }

    private static async Task<IResult> HoldAsync(Guid id, ReservationService service, InventoryDbContext db, CancellationToken ct) =>
        await service.HoldAsync(id, ct) switch
        {
            HoldOutcome.Ok ok => Results.Ok(await ToResponseAsync(db, ok.Reservation, await CopyNumberAsync(db, id, ct), ct)),
            HoldOutcome.Expired => Problems.Conflict("reservation-expired", "The reservation ran out of time. The copy went back on sale."),
            HoldOutcome.Released => Problems.Conflict("reservation-released", "The reservation was released."),
            _ => Problems.NotFound("Reservation"),
        };

    private static async Task<IResult> ConfirmAsync(
        Guid id, ReservationService service, InventoryDbContext db, CancellationToken ct) =>
        await service.ConfirmAsync(id, ct) switch
        {
            ConfirmOutcome.Confirmed c => Results.Ok(await ToResponseAsync(db, c.Reservation, c.CopyNumber, ct)),
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

    private static Task<int?> CopyNumberAsync(InventoryDbContext db, Guid reservationId, CancellationToken ct) =>
        db.Copies
            .Where(c => c.ReservationId == reservationId)
            .Select(c => (int?)c.Number)
            .FirstOrDefaultAsync(ct);

    /// <summary>The reservation with the edition's video, format and price: Orders needs them to start the payment. They never change.</summary>
    private static async Task<ReservationResponse> ToResponseAsync(
        InventoryDbContext db, Reservation reservation, int? copyNumber, CancellationToken ct)
    {
        var edition = await db.Editions.AsNoTracking().SingleAsync(e => e.Id == reservation.EditionId, ct);
        return ReservationResponse.From(reservation, edition, copyNumber);
    }
}
