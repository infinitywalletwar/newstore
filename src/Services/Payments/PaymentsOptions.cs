namespace NewStore.Payments;

/// <summary>Bound from the "Payments" section.</summary>
public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    /// <summary>How long a provider session stays open. The provider's range is 30 minutes to 24 hours.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(30);

    public ProviderOptions Provider { get; set; } = new();
}

public sealed class ProviderOptions
{
    /// <summary>The provider's API address, for example FakePay's.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Sent as a bearer token on every provider call.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Verifies the signature of the provider's webhooks.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>
    /// Added to <see cref="PaymentsOptions.SessionLifetime"/> when a session is requested. The provider counts its 30-minute minimum
    /// from the moment the request arrives, so a request for exactly "now + 30 minutes" is a moment short by then.
    /// </summary>
    public TimeSpan ExpiryMargin { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Session creation runs while Payments holds a row lock and a database transaction, so it gets its own, short pipeline: one
    /// retry (<see cref="SessionRetryDelay"/> after the first failure), each attempt cut off after
    /// <see cref="SessionAttemptTimeout"/>, and the whole call after <see cref="SessionTotalTimeout"/>. Together they must stay well
    /// inside the caller's own attempt timeout (10 s, the standard handler's default).
    /// </summary>
    public TimeSpan SessionAttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan SessionTotalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan SessionRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How far a webhook's timestamp may be from now, in either direction. Older or newer is refused as a replay.</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);
}
