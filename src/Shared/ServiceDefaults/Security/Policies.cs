namespace NewStore.ServiceDefaults.Security;

public static class Policies
{
    /// <summary>A user's token: authenticated, has <c>sub</c>, and <c>azp</c> is one of <see cref="AuthOptions.UserClients"/>.</summary>
    public const string SignedIn = "signed-in";

    /// <summary>A service's token: <c>azp</c> is <paramref name="clientId"/>.</summary>
    public static string Client(string clientId) => $"client:{clientId}";
}
