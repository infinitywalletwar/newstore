using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NewStore.Payments.Provider;

/// <summary>
/// Checks a <c>FakePay-Signature</c> header: <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, "{t}.{body}")&gt;</c>, where <c>v1</c> may repeat. Two things
/// must hold: the HMAC matches for at least one <c>v1</c>, which shows the sender knows the secret and the body is unchanged, and the timestamp is within
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
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim() == "t")
            {
                timestampText = pair[1].Trim();
            }
            else if (pair.Length == 2 && pair[0].Trim() == "v1")
            {
                signatures.Add(pair[1].Trim());
            }
        }

        if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) || signatures.Count == 0)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"));

        // The header may carry several v1 values (a sender that rotates its secret signs with both), and one match is enough.
        // Every value is compared, with no early exit, and each comparison is constant-time: the time it takes must not
        // tell an attacker how many leading bytes were right.
        var matched = false;
        foreach (var signatureHex in signatures)
        {
            try
            {
                matched |= CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signatureHex));
            }
            catch (FormatException)
            {
                // Not hex: this value cannot match, and the others may.
            }
        }

        if (!matched)
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        return age.Duration() <= tolerance;
    }
}
