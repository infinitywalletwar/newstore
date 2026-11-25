namespace NewStore.Orders.Orders;

/// <summary>
/// <c>Delivery</c> is required for a DVD and refused for any other format. <see cref="DeliveryDetails"/>' members are nullable, so a
/// missing latitude is a 400 and not a pin at 0.
/// </summary>
public sealed record BuyRequest(Guid EditionId, DeliveryDetails? Delivery);

public sealed record DeliveryDetails(string? Address, double? Latitude, double? Longitude);

/// <summary>The edition format names Inventory sends that Orders acts on (Inventory's <c>EditionFormat</c>).</summary>
public static class EditionFormats
{
    public const string Dvd = "Dvd";
}

/// <summary>The order as the browser sees it. <c>Status</c> is the enum's name, as in Inventory's reservation.</summary>
public sealed record OrderResponse(
    Guid Id, Guid EditionId, Guid VideoId, string Format, decimal Price, string Status,
    DateTimeOffset ReservedUntil, string? CheckoutUrl, int? CopyNumber)
{
    public static OrderResponse From(Order o) =>
        new(o.Id, o.EditionId, o.VideoId, o.Format, o.Price, o.Status.ToString(), o.ReservedUntil, o.CheckoutUrl, o.CopyNumber);
}

public sealed record PayResponse(string CheckoutUrl);

/// <summary>One line of "my copies".</summary>
public sealed record CopyResponse(
    Guid OrderId, Guid EditionId, Guid VideoId, string Format, int CopyNumber, decimal Price, DateTimeOffset CompletedAt);
