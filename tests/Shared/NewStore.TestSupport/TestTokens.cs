using System.Security.Cryptography;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NewStore.TestSupport;

/// <summary>
/// Tokens signed with a key that exists only in this test run, shaped like Keycloak's: <c>sub</c>, <c>azp</c> (the client that
/// asked for the token) and <c>aud</c>. A service under test trusts <see cref="SigningKey"/> through <c>UseTestSigningKey</c>.
/// </summary>
public static class TestTokens
{
    public const string Issuer = "http://keycloak.test/realms/newstore";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    /// <summary>One RSA key per test run.</summary>
    public static SecurityKey SigningKey => Key;

    /// <summary>A token for a signed-in user. A negative <paramref name="lifetime"/> gives a token that expired that long ago.</summary>
    public static string User(Guid userId, string audience, string clientId = "gateway", TimeSpan? lifetime = null) =>
        Create(userId, clientId, [audience], lifetime ?? TimeSpan.FromMinutes(5));

    /// <summary>A client-credentials token: the subject is the client's service account, which has no user.</summary>
    public static string Client(string clientId, params string[] audiences) =>
        Create(Guid.CreateVersion7(), clientId, audiences, TimeSpan.FromMinutes(5));

    private static string Create(Guid subject, string clientId, string[] audiences, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        var expires = now + lifetime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            NotBefore = lifetime < TimeSpan.Zero ? expires - TimeSpan.FromMinutes(5) : now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject.ToString(),
                ["azp"] = clientId,
                ["aud"] = audiences,
            },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
