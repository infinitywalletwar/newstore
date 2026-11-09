using System.Text.Json;

namespace NewStore.Security.Tests;

/// <summary>The realm file Compose and the Keycloak container both import. Tests read client secrets from it.</summary>
internal static class RealmFile
{
    public static string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "newstore-realm.json");

    /// <summary>A sample user's password, from <c>users[].credentials</c>.</summary>
    public static string UserPassword(string username)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(Path));
        return realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == username)
            .GetProperty("credentials")[0].GetProperty("value").GetString()!;
    }

    public static string Secret(string clientId)
    {
        using var realm = JsonDocument.Parse(File.ReadAllText(Path));
        return realm.RootElement.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == clientId)
            .GetProperty("secret").GetString()!;
    }
}
