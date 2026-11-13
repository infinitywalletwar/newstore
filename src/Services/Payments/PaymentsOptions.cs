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

    /// <summary>How far a webhook's timestamp may be from now, in either direction. Older or newer is refused as a replay.</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);
}
