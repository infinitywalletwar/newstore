namespace NewStore.Gateway.Auth;

/// <summary>Bound from the "Oidc" section: how the gateway signs a browser in to Keycloak as the confidential client <c>gateway</c>.</summary>
public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    /// <summary>The issuer every token carries and the address the browser is sent to: Keycloak's public URL.</summary>
    public required string Authority { get; set; }

    /// <summary>Where the gateway fetches discovery and signing keys: the URL a container can reach. Null means <c>Authority</c> plus the discovery path.</summary>
    public string? MetadataAddress { get; set; }

    public required string ClientId { get; set; }

    public required string ClientSecret { get; set; }

    /// <summary>False for the local stack, which has no HTTPS until Phase 4.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
