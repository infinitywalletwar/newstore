namespace NewStore.Payments.Payments;

public enum PaymentStatus { Created, Succeeded, Failed, Refunded }

/// <summary>One buyer's attempt to pay for one order. An order has at most one (a unique index on <see cref="OrderId"/>).</summary>
public sealed class PaymentAttempt
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public decimal Amount { get; init; }

    public PaymentStatus Status { get; set; }

    /// <summary>The provider's session id. Null until the provider answered the create call.</summary>
    public string? ProviderSessionId { get; set; }

    public string? CheckoutUrl { get; set; }

    public DateTimeOffset? SessionExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL's <c>xmin</c> system column: it changes with every update of the row.</summary>
    public uint Version { get; private set; }
}

/// <summary>A provider webhook already handled. The primary key is what makes a redelivered webhook a no-op.</summary>
public sealed class ProviderEvent
{
    public required string EventId { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
}
