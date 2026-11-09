using System.Security.Claims;

namespace NewStore.ServiceDefaults.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The <c>sub</c> claim as a Guid. JwtBearer runs with <c>MapInboundClaims = false</c>, so the claim keeps its JWT name.</summary>
    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);

    /// <summary>The <c>azp</c> claim: the client that asked Keycloak for this token.</summary>
    public static string? ClientId(this ClaimsPrincipal user) => user.FindFirstValue("azp");
}
