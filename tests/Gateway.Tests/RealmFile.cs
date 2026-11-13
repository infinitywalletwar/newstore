using System.Text.Json;

namespace NewStore.Gateway.Tests;

/// <summary>The realm file Compose and the Keycloak container both import. A sample user's password is read from it.</summary>
internal static class RealmFile
{
    public static string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "newstore-realm.json");

    /// <summary>A client's secret, from <c>clients[].secret</c>.</summary>
    public static string ClientSecret(string clientId)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(Path));
        return realm.RootElement.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == clientId)
            .GetProperty("secret").GetString()!;
    }

    public static string UserPassword(string username)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(Path));
        return realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username)
            .GetProperty("credentials")[0].GetProperty("value").GetString()!;
    }
}
