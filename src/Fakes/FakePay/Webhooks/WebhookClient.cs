using System.Text;

namespace NewStore.FakePay.Webhooks;

/// <summary>Posts one webhook. A typed client, so tests put a stub behind it.</summary>
public sealed class WebhookClient(HttpClient http)
{
    /// <summary>The receiver's answer, or null when there was none (unreachable, or a timeout). Only a 2xx counts as delivered.</summary>
    public async Task<HttpResponseMessage?> PostAsync(Uri url, string body, string signature, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookSigner.HeaderName, signature);
        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout.
            return null;
        }
    }
}
