using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace NewStore.ServiceDefaults.Security;

/// <summary>Satisfied by a token that has <c>sub</c> and whose <c>azp</c> is a user client.</summary>
internal sealed class SignedInRequirement : IAuthorizationRequirement;

/// <summary>
/// A handler rather than <c>RequireAssertion</c>, because it reads <see cref="AuthOptions"/> from DI when the request runs,
/// not when the policy is built. The list of user clients can then come from configuration that is applied late (tests do this).
/// </summary>
internal sealed class SignedInHandler(IOptions<AuthOptions> options) : AuthorizationHandler<SignedInRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SignedInRequirement requirement)
    {
        var user = context.User;
        if (user.FindFirst("sub") is not null && user.ClientId() is { } client && options.Value.UserClients.Contains(client))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
