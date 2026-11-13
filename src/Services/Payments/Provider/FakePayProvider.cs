using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using NewStore.Messaging;
using NewStore.Payments.Payments;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NewStore.Payments.Provider;

/// <summary>
/// <see cref="IPaymentProvider"/> for FakePay. The <see cref="HttpClient"/> carries the base address, the API key and the
/// resilience pipeline (see <c>Program.cs</c>). When that pipeline gives up without an answer, Polly throws
/// <see cref="BrokenCircuitException"/> (the circuit is open) or <see cref="TimeoutRejectedException"/>. Messaging does not know
/// those types, so this class turns them into <see cref="TransientFailureException"/>: a handler that calls the provider is then
/// retried while the provider is down, not dead-lettered. An answer with a bad status is an <see cref="HttpRequestException"/>,
/// which Messaging classifies itself.
/// </summary>
public sealed partial class FakePayProvider(
    HttpClient http, IHttpClientFactory clients, IOptions<PaymentsOptions> options, TimeProvider time, ILogger<FakePayProvider> logger)
    : IPaymentProvider
{
    /// <summary>The name of the HTTP client that carries session creation, which has its own, shorter resilience pipeline.</summary>
    public const string SessionClientName = "FakePay-session";

    public async Task<ProviderSession> CreateSessionAsync(PaymentAttempt attempt, string successUrl, string cancelUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/sessions")
        {
            Content = JsonContent.Create(new
            {
                amount = attempt.Amount,
                successUrl,
                cancelUrl,
                expiresAt = time.GetUtcNow() + options.Value.SessionLifetime + options.Value.Provider.ExpiryMargin,
                metadata = new { paymentId = attempt.Id, orderId = attempt.OrderId },
            }),
        };

        // The key is the attempt's id: if the reply is lost and this is called again, FakePay returns the first session.
        request.Headers.Add("Idempotency-Key", attempt.Id.ToString());

        using var response = await SendAsync(clients.CreateClient(SessionClientName), request, ct);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<SessionBody>(ct)
            ?? throw new InvalidOperationException("FakePay answered a session request with an empty body.");
        return new ProviderSession(session.Id, session.Url, session.ExpiresAt);
    }

    public async Task RefundAsync(PaymentAttempt attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/refunds")
        {
            Content = JsonContent.Create(new { sessionId = attempt.ProviderSessionId }),
        };
        request.Headers.Add("Idempotency-Key", attempt.Id.ToString());

        using var response = await SendAsync(http, request, ct);
        response.EnsureSuccessStatusCode();
    }

    public ProviderEvent? ReadWebhook(string body, IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var settings = options.Value.Provider;
        if (!FakePaySignature.IsValid(settings.WebhookSecret, body, headers[FakePaySignature.HeaderName], time.GetUtcNow(), settings.WebhookTolerance))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var type = root.GetProperty("type").GetString() switch
            {
                "checkout.session.completed" => ProviderEventType.SessionCompleted,
                "checkout.session.expired" => ProviderEventType.SessionExpired,
                _ => (ProviderEventType?)null,
            };

            if (type is not { } known)
            {
                // Signed, so it came from the provider, but FakePay never sends this type. Refused like a bad one (see IPaymentProvider).
                UnknownEventType(logger, root.GetProperty("type").GetString());
                return null;
            }

            return new ProviderEvent(root.GetProperty("id").GetString()!, known, root.GetProperty("data").GetProperty("sessionId").GetString()!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (BrokenCircuitException ex)
        {
            throw new TransientFailureException("FakePay's circuit breaker is open.", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new TransientFailureException("FakePay did not answer in time.", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A correctly signed webhook has the type {EventType}, which FakePay never sends; it is refused.")]
    private static partial void UnknownEventType(ILogger logger, string? eventType);

    private sealed record SessionBody(string Id, string Url, DateTimeOffset ExpiresAt);
}
