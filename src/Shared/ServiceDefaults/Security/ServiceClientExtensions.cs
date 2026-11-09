using Duende.AccessTokenManagement;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NewStore.ServiceDefaults.Security;

public static class ServiceClientExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TClient"/> as a typed <see cref="HttpClient"/> for another service. Its address is
    /// <c>configuration[<paramref name="baseAddressKey"/>]</c>. Every call goes through the standard resilience pipeline (retry,
    /// circuit breaker, timeouts) and carries a client-credentials token.
    /// </summary>
    public static IHttpClientBuilder AddServiceClient<TClient>(this IHostApplicationBuilder builder, string baseAddressKey)
        where TClient : class
    {
        var services = builder.Services;
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
        services.AddClientCredentialsTokenManagement();
        services.AddOptions<ClientCredentialsClient>(DuendeServiceTokenSource.ClientName)
            .Configure<IOptions<AuthOptions>>((client, auth) =>
            {
                client.TokenEndpoint = new Uri(auth.Value.TokenEndpoint ?? throw new InvalidOperationException("Auth:TokenEndpoint is not set."));
                client.ClientId = ClientId.Parse(auth.Value.ClientId ?? throw new InvalidOperationException("Auth:ClientId is not set."));
                client.ClientSecret = ClientSecret.Parse(auth.Value.ClientSecret ?? throw new InvalidOperationException("Auth:ClientSecret is not set."));
            });
        services.TryAddSingleton<IServiceTokenSource, DuendeServiceTokenSource>();
        services.TryAddTransient<ServiceTokenHandler>();

        var http = services.AddHttpClient<TClient>((provider, client) =>
        {
            var address = provider.GetRequiredService<IConfiguration>()[baseAddressKey]
                ?? throw new InvalidOperationException($"Configuration key '{baseAddressKey}' is not set.");
            client.BaseAddress = new Uri(address);
        });

        // Handlers run in the order they are added, outermost first. The resilience handler is first, so the token handler sits
        // inside it and runs again on every retry.
        http.AddStandardResilienceHandler();
        http.AddHttpMessageHandler<ServiceTokenHandler>();
        return http;
    }
}
