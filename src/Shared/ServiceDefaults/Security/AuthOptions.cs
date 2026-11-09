namespace NewStore.ServiceDefaults.Security;

/// <summary>Bound from the "Auth" section. A service that only validates tokens sets the first five; a service that also calls others sets the last three.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The <c>iss</c> every token carries: Keycloak's public URL, the same in every environment.</summary>
    public required string Issuer { get; set; }

    /// <summary>Where to fetch signing keys: the URL a container can reach. Null means <c>Issuer + "/.well-known/openid-configuration"</c>.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The <c>aud</c> a token must carry to be accepted by this service.</summary>
    public required string Audience { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Clients that sign users in (<c>azp</c>). <see cref="Policies.SignedIn"/> accepts a token only from one of these.</summary>
    public List<string> UserClients { get; set; } = [];

    /// <summary>This service's own client, for calling other services.</summary>
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? TokenEndpoint { get; set; }
}
