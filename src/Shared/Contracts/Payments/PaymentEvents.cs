using NewStore.Messaging;

namespace NewStore.Contracts.Payments;

/// <summary>
/// Where Payments publishes. All three events share one topic, so one order's payment events stay in order. The key is
/// the order id. Producer and consumers declare the topic with these constants, so either may create it.
/// </summary>
public static class PaymentEvents
{
    public const string Topic = "payments.payments";

    public const int Partitions = 3;
}

/// <summary>The provider confirmed the payment.</summary>
public sealed record PaymentSucceeded : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid PaymentId { get; init; }

    public required Guid OrderId { get; init; }

    public required decimal Amount { get; init; }
}

/// <summary>The payment ended without money moving, for example because the provider's session expired.</summary>
public sealed record PaymentFailed : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid PaymentId { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reason { get; init; }
}

/// <summary>A succeeded payment was given back, after Orders asked for it with <c>RefundRequested</c>.</summary>
public sealed record PaymentRefunded : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid PaymentId { get; init; }

    public required Guid OrderId { get; init; }

    public required decimal Amount { get; init; }
}
