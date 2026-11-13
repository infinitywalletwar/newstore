using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewStore.Payments.Tests;

/// <summary>
/// Signs webhooks the way the provider does: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "{t}.{body}")&gt;</c>. It is written
/// out here, not shared with the service, so a mistake in the service's check is not repeated in the test.
/// </summary>
internal static class TestWebhooks
{
    public const string Secret = "test-webhook-secret";

    public static string Body(string eventId, string type, string sessionId) =>
        JsonSerializer.Serialize(new { id = eventId, type, data = new { sessionId } });

    public static string Sign(string body, DateTimeOffset at, string secret = Secret)
    {
        var timestamp = at.ToUnixTimeSeconds();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string eventId, string type, string sessionId, DateTimeOffset? at = null, string secret = Secret)
    {
        var body = Body(eventId, type, sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/fakepay")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("FakePay-Signature", Sign(body, at ?? DateTimeOffset.UtcNow, secret));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public const string Completed = "checkout.session.completed";
    public const string Expired = "checkout.session.expired";
}
