using NewStore.Messaging;

namespace NewStore.Contracts.Orders;

/// <summary>
/// Where Orders publishes. Every Orders event shares this topic and its key, the order id, so one order's events stay in
/// order. Producer and consumers declare the topic with these constants.
/// </summary>
public static class OrderEvents
{
    public const string Topic = "orders.orders";

    public const int Partitions = 3;
}

/// <summary>
/// Orders asks Payments to give the money back: the payment succeeded after the reservation's hold ran out, so the buyer
/// gets no copy.
/// </summary>
public sealed record RefundRequested : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid PaymentId { get; init; }
}
