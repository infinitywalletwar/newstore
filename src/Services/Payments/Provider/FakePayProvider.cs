using Microsoft.Extensions.Options;

using NewStore.Payments.Payments;

namespace NewStore.Payments.Provider;

/// <summary>Step 2 stub: the constructor has its final shape, the members do nothing yet.</summary>
public sealed class FakePayProvider : IPaymentProvider
{
    public FakePayProvider(HttpClient http, IOptions<PaymentsOptions> options, TimeProvider time)
    {
        _ = (http, options, time);
    }

    public Task<ProviderSession> CreateSessionAsync(PaymentAttempt attempt, string successUrl, string cancelUrl, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task RefundAsync(PaymentAttempt attempt, CancellationToken ct) => throw new NotImplementedException();

    public ProviderEvent? ReadWebhook(string body, IHeaderDictionary headers) => throw new NotImplementedException();
}
