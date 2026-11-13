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
        AuthOptionsRegistration.Bind(services);
        AuthOptionsRegistration.RequireCallerSettings(services);
        services.AddClientCredentialsTokenManagement();
        services.AddOptions<ClientCredentialsClient>(DuendeServiceTokenSource.ClientName)
            .Configure<IOptions<AuthOptions>>((client, auth) =>
            {
                // Present and non-empty: AuthOptionsRegistration.RequireCallerSettings checks it at startup.
                client.TokenEndpoint = new Uri(auth.Value.TokenEndpoint!);
                client.ClientId = ClientId.Parse(auth.Value.ClientId!);
                client.ClientSecret = ClientSecret.Parse(auth.Value.ClientSecret!);
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
        // The standard pipeline's concurrency limiter allows 1,000 calls at once and, by default, a queue of 0: the 1,001st call is
        // rejected on the spot ("Rate limiter ... Queue: 0, Permit: 1_000",
        // https://learn.microsoft.com/dotnet/core/resilience/http-resilience#standard-resilience-handler-defaults). A burst of
        // buyers (the last-copy load test sends 1,010 buys in 0.3 s) made Orders answer 503 to the ten over the limit. A queue
        // makes them wait their turn; it stays bounded, so a real overload still sheds load instead of piling up without end.
        http.AddStandardResilienceHandler(options => options.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 1000);
        http.AddHttpMessageHandler<ServiceTokenHandler>();
        return http;
    }
}
