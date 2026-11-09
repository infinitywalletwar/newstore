using System.Net.Http.Headers;

using Duende.AccessTokenManagement;

using Microsoft.Extensions.Options;

namespace NewStore.ServiceDefaults.Security;

/// <summary>Gives a service's own access token: the client-credentials token for the client in <see cref="AuthOptions"/>.</summary>
public interface IServiceTokenSource
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Puts <c>Authorization: Bearer</c> on every outgoing request. It runs once per attempt, inside the retry handler.</summary>
public sealed class ServiceTokenHandler(IServiceTokenSource tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Duende.AccessTokenManagement asks Keycloak for the token and keeps it until shortly before it expires, so most calls
/// reuse the cached one.
/// </summary>
internal sealed class DuendeServiceTokenSource(IClientCredentialsTokenManager manager) : IServiceTokenSource
{
    public const string ClientName = "newstore";

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var result = await manager.GetAccessTokenAsync(ClientCredentialsClientName.Parse(ClientName), ct: cancellationToken);
        return result.WasSuccessful(out var token, out var failure)
            ? token.AccessToken.ToString()
            : throw new InvalidOperationException($"Could not get a service token: {failure}");
    }
}
