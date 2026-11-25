namespace NewStore.Orders;

/// <summary>Log messages of Orders, generated at build time (analyzer CA1848).</summary>
internal static partial class OrdersLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release reservation {ReservationId}; Inventory frees it when it expires.")]
    public static partial void ReleaseFailed(ILogger logger, Guid reservationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} is a DVD with no delivery address (bought before addresses existed); it completes without a shipment.")]
    public static partial void DvdWithoutAddress(ILogger logger, Guid orderId);
}
