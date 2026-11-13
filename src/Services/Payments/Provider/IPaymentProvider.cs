using NewStore.Payments.Payments;

namespace NewStore.Payments.Provider;

/// <summary>
/// What Payments needs from a payment provider. FakePay implements it now and Stripe will (Phase 7), so the rest of the
/// service never sees a provider's wire format.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>
    /// Opens a hosted-checkout session for <paramref name="attempt"/>. The provider's idempotency key is the attempt's id, so
    /// a repeated call, after a lost reply for example, returns the same session and never a second one.
    /// </summary>
    Task<ProviderSession> CreateSessionAsync(PaymentAttempt attempt, string successUrl, string cancelUrl, CancellationToken ct);

    /// <summary>Refunds the attempt's payment. The idempotency key is the attempt's id, so a repeated call refunds once.</summary>
    Task RefundAsync(PaymentAttempt attempt, CancellationToken ct);

    /// <summary>
    /// Verifies and parses a webhook. Null when the signature is bad, the timestamp is stale, or the event has a type the provider
    /// never sends (the endpoint answers all three with 400). A real provider can send more types; its implementation will need a
    /// way to say "valid, ignore it".
    /// </summary>
    ProviderEvent? ReadWebhook(string body, IHeaderDictionary headers);
}

public sealed record ProviderSession(string SessionId, string CheckoutUrl, DateTimeOffset ExpiresAt);

public enum ProviderEventType { SessionCompleted, SessionExpired }

public sealed record ProviderEvent(string EventId, ProviderEventType Type, string SessionId);
