using System.Security.Cryptography;
using System.Text;

namespace NewStore.FakePay.Webhooks;

/// <summary>
/// Signs a webhook body: the header is <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "{t}.{body}")&gt;</c>, the layout of
/// Stripe's <c>Stripe-Signature</c>. The timestamp is part of the signed text, so a receiver can refuse an old, replayed request.
/// </summary>
public static class WebhookSigner
{
    public const string HeaderName = "FakePay-Signature";

    public static string Sign(string secret, string body, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }
}
