using Microsoft.AspNetCore.Authorization;

namespace NewStore.Inventory;

/// <summary>Satisfied when the signed-in user is the edition's creator. Checked against a loaded <see cref="Edition"/> (ADR 0016).</summary>
public sealed class EditionOwnerRequirement : IAuthorizationRequirement;

public sealed class EditionOwnerHandler : AuthorizationHandler<EditionOwnerRequirement, Edition>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, EditionOwnerRequirement requirement, Edition edition)
    {
        // JwtBearer runs with MapInboundClaims = false, so the subject keeps its JWT name.
        if (Guid.TryParse(context.User.FindFirst("sub")?.Value, out var userId) && userId == edition.CreatorId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
