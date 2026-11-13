using System.Text;

namespace NewStore.FakePay.Webhooks;

/// <summary>Posts one webhook. A typed client, so tests put a stub behind it.</summary>
public sealed class WebhookClient(HttpClient http)
{
    /// <summary>True when the receiver answered 2xx. A refusal, an unreachable receiver and a timeout are all "not delivered".</summary>
    public async Task<bool> PostAsync(Uri url, string body, string signature, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSigner.HeaderName, signature);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout.
            return false;
        }
    }
}
