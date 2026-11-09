using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using NewStore.ServiceDefaults.Security;

namespace NewStore.TestSupport;

public static class TestAuthExtensions
{
    /// <summary>
    /// Makes JwtBearer trust <see cref="TestTokens.SigningKey"/> and nothing else. The configuration is given directly,
    /// so the service fetches no metadata and needs no Keycloak.
    /// This is <c>Configure</c>, not <c>PostConfigure</c>: JwtBearer's own post-configure builds its configuration manager from
    /// <c>Configuration</c>, so the value must be there before it runs.
    /// </summary>
    public static IServiceCollection UseTestSigningKey(this IServiceCollection services) =>
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            jwt.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = TestTokens.Issuer,
                SigningKeys = { TestTokens.SigningKey },
            });

    /// <summary>Replaces <see cref="IServiceTokenSource"/> with one that returns <c>TestTokens.Client(Auth:ClientId)</c>.</summary>
    public static IServiceCollection UseTestServiceTokens(this IServiceCollection services) =>
        services.AddSingleton<IServiceTokenSource, TestServiceTokenSource>();

    /// <summary>
    /// Makes <paramref name="stub"/> the primary handler of <typeparamref name="TClient"/>, below the resilience and token
    /// handlers, so a test sees exactly what would go on the wire. Call it inside <c>ConfigureTestServices</c>. It relies on a
    /// typed client's name being <c>typeof(TClient).Name</c>.
    /// </summary>
    public static IHttpClientBuilder UseStubHandler<TClient>(this IServiceCollection services, StubHttpHandler stub)
        where TClient : class =>
        // A typed client's name is its type's short name.
        services.AddHttpClient(typeof(TClient).Name).ConfigurePrimaryHttpMessageHandler(() => stub);

    private sealed class TestServiceTokenSource(IOptions<AuthOptions> options) : IServiceTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TestTokens.Client(options.Value.ClientId ?? "test-client"));
    }
}
