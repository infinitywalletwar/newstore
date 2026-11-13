namespace NewStore.Gateway.Tests;

/// <summary>Sample users from the realm file. Their username and password are the channel slug.</summary>
internal static class Users
{
    public const string OrbitalKitchen = "orbital-kitchen";
    public static readonly Guid OrbitalKitchenId = Guid.Parse("00000000-0000-7000-8000-000000000001");

    /// <summary>The expired-session test ends this user's sessions, so no other test signs in as them.</summary>
    public const string TrailNotes = "trail-notes";
    public static readonly Guid TrailNotesId = Guid.Parse("00000000-0000-7000-8000-000000000002");
}
