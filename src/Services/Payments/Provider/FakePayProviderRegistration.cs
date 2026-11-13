using System.Net.Http.Headers;

using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace NewStore.Payments.Provider;

public static class FakePayProviderRegistration
{
    /// <summary>
    /// Registers <see cref="IPaymentProvider"/> with two HTTP clients for the same provider:
    /// <list type="bullet">
    /// <item>the refund client (the typed client of the interface) with the standard resilience handler: up to 3 retries with
    /// exponential backoff from 2 s, 10 s per attempt, 30 s in all (Microsoft's documented defaults). A refund holds no lock, and the
    /// message that asked for it is retried anyway;</item>
    /// <item>the session client (<see cref="FakePayProvider.SessionClientName"/>) with the same handler tuned by
    /// <see cref="ProviderOptions"/> to one retry, 2 s per attempt and 5 s in all, because session creation runs under a row lock.</item>
    /// </list>
    /// Retrying a POST is safe here because every call carries an idempotency key.
    /// </summary>
    public static IServiceCollection AddFakePayProvider(this IServiceCollection services)
    {
        services.AddHttpClient(FakePayProvider.SessionClientName, ConfigureClient).AddStandardResilienceHandler();

        // The standard handler's options are named "<client name>-standard".
        services.AddOptions<HttpStandardResilienceOptions>(FakePayProvider.SessionClientName + "-standard")
            .Configure<IOptions<PaymentsOptions>>((options, payments) =>
            {
                var provider = payments.Value.Provider;
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = provider.SessionRetryDelay;
                options.AttemptTimeout.Timeout = provider.SessionAttemptTimeout;
                options.TotalRequestTimeout.Timeout = provider.SessionTotalTimeout;
            });

        services.AddHttpClient<IPaymentProvider, FakePayProvider>(ConfigureClient)
            .AddStandardResilienceHandler();
        return services;
    }

    private static void ConfigureClient(IServiceProvider services, HttpClient client)
    {
        var provider = services.GetRequiredService<IOptions<PaymentsOptions>>().Value.Provider;
        client.BaseAddress = new Uri(provider.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
    }
}
