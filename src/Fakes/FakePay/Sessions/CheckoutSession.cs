namespace NewStore.FakePay.Sessions;

public enum SessionStatus { Open, Complete, Expired }

/// <summary>One hosted-checkout session. The store replaces the record on each change, so a copy handed out never changes under its reader.</summary>
public sealed record CheckoutSession(
    string Id,
    decimal Amount,
    string SuccessUrl,
    string CancelUrl,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Metadata,
    SessionStatus Status = SessionStatus.Open);

public sealed record Refund(string Id, string SessionId, decimal Amount);
