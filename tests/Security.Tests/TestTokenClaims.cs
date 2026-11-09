using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;

namespace NewStore.Security.Tests;

internal static class TestTokenClaims
{
    /// <summary>The payload of a JWT, without validating it.</summary>
    public static JsonElement Read(string jwt) =>
        JsonDocument.Parse(WebEncoders.Base64UrlDecode(jwt.Split('.')[1])).RootElement;
}
