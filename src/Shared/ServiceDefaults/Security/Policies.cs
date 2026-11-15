namespace NewStore.ServiceDefaults.Security;

public static class Policies
{
    /// <summary>A user's token: authenticated, has <c>sub</c>, and <c>azp</c> is one of <see cref="AuthOptions.UserClients"/>.</summary>
    public const string SignedIn = "signed-in";

    /// <summary>A service's token: <c>azp</c> is <paramref name="clientId"/>.</summary>
    public static string Client(string clientId) => $"client:{clientId}";

    /// <summary>A service's token: <c>azp</c> is any of <paramref name="clientIds"/>. The name lists them: <c>client:a|b</c>.</summary>
    public static string Clients(params string[] clientIds) => $"client:{string.Join('|', clientIds)}";
}
