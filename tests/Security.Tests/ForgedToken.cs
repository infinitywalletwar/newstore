using System.Security.Cryptography;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace NewStore.Security.Tests;

/// <summary>Tokens with one thing wrong, which <see cref="TestTokens"/> does not make: another issuer, another key, no subject.</summary>
internal static class ForgedToken
{
    public static string Create(string issuer = TestTokens.Issuer, SecurityKey? key = null, string? subject = "user-1",
        string clientId = "gateway", string audience = "inventory")
    {
        var claims = new Dictionary<string, object> { ["azp"] = clientId };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? TestTokens.SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A fresh key with the same key id as <see cref="TestTokens.SigningKey"/>, so only the key material differs.</summary>
    public static SecurityKey OtherKeyWithTheSameId() =>
        new RsaSecurityKey(RSA.Create(2048)) { KeyId = TestTokens.SigningKey.KeyId };
}
