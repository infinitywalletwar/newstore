using System.Net;
using System.Net.Http.Json;

namespace NewStore.Orders.Clients;

/// <summary>Orders' own view of a payment: the id it keeps and the page it sends the buyer to.</summary>
public sealed record PaymentInfo(Guid PaymentId, string CheckoutUrl);

/// <summary>
/// Orders' call to Payments (ADR 0014). Payments answers 201 for the call that created the payment and 200 for every repeat,
/// with the same body, so Orders treats both as success. A call that gets no answer throws
/// <see cref="NewStore.Messaging.TransientFailureException"/>; repeating it is safe.
/// </summary>
public sealed class PaymentsClient(HttpClient http)
{
    public async Task<PaymentInfo> CreateAsync(Guid orderId, decimal amount, string returnUrl, CancellationToken ct)
    {
        using var response = await ServiceCall.PostAsync(http, "/payments", new { orderId, amount, returnUrl }, ct);
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            // A 400 means Orders sent something Payments refuses: a bug, not something to retry.
            throw new HttpRequestException($"Create payment answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var body = await response.Content.ReadFromJsonAsync<PaymentBody>(ct)
            ?? throw new InvalidOperationException("Payments answered with an empty body.");
        if (string.IsNullOrEmpty(body.CheckoutUrl))
        {
            throw new InvalidOperationException($"Payments answered without a checkout URL for order {orderId}.");
        }

        // Payments names the payment's id "id".
        return new PaymentInfo(body.Id, body.CheckoutUrl);
    }

    private sealed record PaymentBody(Guid Id, string? CheckoutUrl);
}
