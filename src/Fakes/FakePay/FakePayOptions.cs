namespace NewStore.FakePay;

/// <summary>Bound from the "FakePay" section.</summary>
public sealed class FakePayOptions
{
    public const string SectionName = "FakePay";

    /// <summary>The bearer token a caller of <c>/v1/*</c> must send.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The shared secret that signs every webhook.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Where webhooks are posted.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>The address a buyer's browser reaches FakePay at. A session's checkout URL starts with it.</summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>Sends each webhook twice, like a provider that delivers at least once.</summary>
    public bool DeliverTwice { get; set; }

    /// <summary>Maps <c>POST /test/sessions/{id}/pay</c>, which pays without the page. Off unless a test or the load overlay sets it.</summary>
    public bool EnableTestEndpoints { get; set; }

    /// <summary>How long to wait before sending a webhook again after a failure.</summary>
    public TimeSpan WebhookRetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the sweeper looks for open sessions past their expiry.</summary>
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(5);
}
