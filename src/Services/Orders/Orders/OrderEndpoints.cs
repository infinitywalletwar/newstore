using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.Messaging;
using NewStore.Orders.Clients;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Orders.Orders;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder routes)
    {
        // A buyer's token, from a client that signs users in (the gateway). A service token is refused with 403.
        var group = routes.MapGroup("").RequireAuthorization(Policies.SignedIn);
        group.MapPost("/orders", BuyAsync);
        group.MapPost("/orders/{id:guid}/pay", PayAsync);
        group.MapGet("/orders/{id:guid}", GetAsync);
        group.MapGet("/me/copies", CopiesAsync);
        return routes;
    }

    private static async Task<IResult> BuyAsync(
        BuyRequest request, ClaimsPrincipal user, OrdersDbContext db, InventoryClient inventory, TimeProvider time,
        HttpContext http, CancellationToken ct)
    {
        if (request.EditionId == Guid.Empty)
        {
            return Problems.Validation(new() { ["editionId"] = ["editionId is required."] });
        }

        var buyer = user.UserId();
        var now = time.GetUtcNowToMicrosecond();

        // A buyer who clicks Buy twice, or comes back to the page, gets the open order again instead of a second reservation.
        var open = await FindOpenOrderAsync(db, buyer, request.EditionId, now, ct);
        if (open is not null)
        {
            return Results.Ok(OrderResponse.From(open));
        }

        // Chosen before the call and sent on every retry: a reserve whose answer was lost is recovered by asking again (ADR 0013).
        var reservationId = Guid.CreateVersion7();
        ReserveResult result;
        try
        {
            result = await inventory.ReserveAsync(request.EditionId, reservationId, buyer, ct);
        }
        catch (TransientFailureException)
        {
            return Problems.DependencyUnavailable(http.Response);
        }

        if (result is ReserveResult.Rejected rejected)
        {
            // Two clicks at the same instant both passed the check above. Inventory kept the first reservation and answered the
            // second "already holding"; by now the first request has probably saved its order, so look again before saying no.
            if (rejected.Type == Problems.Type("already-holding")
                && await FindOpenOrderAsync(db, buyer, request.EditionId, now, ct) is { } winner)
            {
                return Results.Ok(OrderResponse.From(winner));
            }

            // Sold out, sale ended, already holding, no such edition: the buyer sees Inventory's answer, and no order is kept.
            return Problems.Passed(rejected.StatusCode, rejected.Type, rejected.Title);
        }

        var reservation = ((ReserveResult.Reserved)result).Reservation;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            BuyerId = buyer,
            EditionId = request.EditionId,
            VideoId = reservation.VideoId,
            Format = reservation.Format,
            Price = reservation.Price,
            ReservationId = reservationId,
            ReservedUntil = reservation.ExpiresAt,
            Status = OrderStatus.Reserved,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Orders.Add(order);
        // Not the request's token: Inventory already holds the copy, and a buyer who closed the tab must not leave a reservation
        // without an order. What still leaks is a crash between the two calls (ADR 0017).
        await db.SaveChangesAsync(CancellationToken.None);
        return Results.Created($"/orders/{order.Id}", OrderResponse.From(order));
    }

    private static Task<Order?> FindOpenOrderAsync(OrdersDbContext db, Guid buyer, Guid editionId, DateTimeOffset now, CancellationToken ct) =>
        db.Orders.AsNoTracking()
            .Where(o => o.BuyerId == buyer && o.EditionId == editionId
                && (o.Status == OrderStatus.Reserved || o.Status == OrderStatus.Paying) && o.ReservedUntil > now)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

    private static async Task<IResult> PayAsync(
        Guid id, ClaimsPrincipal user, OrdersDbContext db, InventoryClient inventory, PaymentsClient payments,
        IOptions<OrdersOptions> options, TimeProvider time, HttpContext http, CancellationToken ct)
    {
        var buyer = user.UserId();
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id && o.BuyerId == buyer, ct);
        if (order is null)
        {
            return Problems.NotFound("Order");
        }

        if (order.Status != OrderStatus.Reserved)
        {
            return Answer(order);
        }

        try
        {
            // Step 1: extend the reservation to the hold's longer deadline. Repeating it changes nothing.
            if (await inventory.HoldAsync(order.ReservationId, ct) is not HoldResult.Held held)
            {
                await SaveAsync(db, order, o => o.Status = OrderStatus.Expired, time, ct);
                return Answer(order);
            }

            // The hold's deadline is saved before Payments is called: if Payments is down, a buyer who comes back after the
            // reservation's own 10 minutes still finds the order open.
            if (!await SaveAsync(db, order, o => o.ReservedUntil = held.ExpiresAt, time, ct, o => o.ReservedUntil == held.ExpiresAt))
            {
                return Answer(order);
            }

            // Step 2: one payment per order, however many times this runs: Payments' unique order id decides.
            var returnUrl = $"{options.Value.WebBaseUrl.TrimEnd('/')}/orders/{order.Id}";
            var payment = await payments.CreateAsync(order.Id, order.Price, returnUrl, ct);

            // Step 3: record it. xmin makes this fail if a payment event or a parallel pay changed the order in the meantime.
            await SaveAsync(
                db, order,
                o =>
                {
                    o.Status = OrderStatus.Paying;
                    o.PaymentId = payment.PaymentId;
                    o.CheckoutUrl = payment.CheckoutUrl;
                    o.ReservedUntil = held.ExpiresAt;
                },
                time, ct);
            return Answer(order);
        }
        catch (TransientFailureException)
        {
            // The order stays Reserved. A second click repeats the hold and the create, and both are safe to repeat.
            return Problems.DependencyUnavailable(http.Response);
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> and saves. If another writer changed the order first (<c>xmin</c> moved), the order is
    /// reloaded and one of three things follows:
    /// <list type="bullet">
    /// <item>The order is still <c>Reserved</c> (the other writer was a parallel pay that only saved the hold's deadline) and
    /// <paramref name="alreadyDone"/> says the change is already stored: return true without writing.</item>
    /// <item>The order is still <c>Reserved</c> and the change is not stored: apply it again and save, at most three tries in all.</item>
    /// <item>The order moved on, or the third try lost as well: return false with the order as the winner left it, and the caller
    /// answers from that.</item>
    /// </list>
    /// </summary>
    private static async Task<bool> SaveAsync(
        OrdersDbContext db, Order order, Action<Order> change, TimeProvider time, CancellationToken ct, Func<Order, bool>? alreadyDone = null)
    {
        const int MaxTries = 3;
        for (var attempt = 1; ; attempt++)
        {
            change(order);
            order.UpdatedAt = time.GetUtcNowToMicrosecond();
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                await db.Entry(order).ReloadAsync(ct);
                if (order.Status != OrderStatus.Reserved || attempt == MaxTries)
                {
                    return false;
                }

                if (alreadyDone?.Invoke(order) == true)
                {
                    return true;
                }
            }
        }
    }

    private static IResult Answer(Order order) => order.Status switch
    {
        OrderStatus.Paying => Results.Ok(new PayResponse(order.CheckoutUrl!)),
        OrderStatus.Expired => Problems.Conflict("order-expired", "The reservation ran out of time. The copy went back on sale."),
        // After three lost saves in a row SaveAsync gives up with the order still Reserved: nothing was recorded, so ask the buyer to retry.
        OrderStatus.Reserved => Problems.Conflict("order-busy", "The order is being changed by another request. Try again."),
        _ => Problems.Conflict("order-closed", $"The order is {order.Status}."),
    };

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, OrdersDbContext db, CancellationToken ct)
    {
        var buyer = user.UserId();
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.BuyerId == buyer, ct);
        return order is null ? Problems.NotFound("Order") : Results.Ok(OrderResponse.From(order));
    }

    private static async Task<IResult> CopiesAsync(ClaimsPrincipal user, OrdersDbContext db, CancellationToken ct)
    {
        var buyer = user.UserId();
        var copies = await db.Orders.AsNoTracking()
            .Where(o => o.BuyerId == buyer && o.Status == OrderStatus.Completed)
            .OrderByDescending(o => o.CompletedAt)
            .Select(o => new CopyResponse(
                o.Id, o.EditionId, o.VideoId, o.Format, o.CopyNumber!.Value, o.Price, o.CompletedAt!.Value, null))
            .ToListAsync(ct);
        return Results.Ok(copies);
    }
}
