using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NewStore.Payments.Provider;

/// <summary>
/// Checks a <c>FakePay-Signature</c> header: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "{t}.{body}")&gt;</c>. Two things
/// must hold: the HMAC matches, which shows the sender knows the secret and the body is unchanged, and the timestamp is within
/// <paramref name="tolerance"/> of now, which stops a captured request from being sent again later. The timestamp is inside the
/// signed text, so an attacker cannot refresh it.
/// </summary>
internal static class FakePaySignature
{
    public const string HeaderName = "FakePay-Signature";

    public static bool IsValid(string secret, string body, string? header, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        string? timestampText = null;
        string? signatureHex = null;
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim() == "t")
            {
                timestampText = pair[1].Trim();
            }
            else if (pair.Length == 2 && pair[0].Trim() == "v1")
            {
                signatureHex = pair[1].Trim();
            }
        }

        if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) || signatureHex is null)
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(signatureHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));

        // Constant-time comparison: the time it takes must not tell an attacker how many leading bytes were right.
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        return age.Duration() <= tolerance;
    }
}
