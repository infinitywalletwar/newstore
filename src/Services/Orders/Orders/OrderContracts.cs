namespace NewStore.Orders.Orders;

public sealed record BuyRequest(Guid EditionId);

/// <summary>The order as the browser sees it. <c>Status</c> is the enum's name, as in Inventory's reservation.</summary>
public sealed record OrderResponse(
    Guid Id, Guid EditionId, Guid VideoId, string Format, decimal Price, string Status,
    DateTimeOffset ReservedUntil, string? CheckoutUrl, int? CopyNumber)
{
    public static OrderResponse From(Order o) =>
        new(o.Id, o.EditionId, o.VideoId, o.Format, o.Price, o.Status.ToString(), o.ReservedUntil, o.CheckoutUrl, o.CopyNumber);
}

public sealed record PayResponse(string CheckoutUrl);

/// <summary>One line of "my copies". <c>DeliveryStatus</c> is always null until Delivery exists (Phase 3).</summary>
public sealed record CopyResponse(
    Guid OrderId, Guid EditionId, Guid VideoId, string Format, int CopyNumber, decimal Price, DateTimeOffset CompletedAt,
    string? DeliveryStatus);
