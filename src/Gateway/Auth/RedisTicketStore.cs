using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

using StackExchange.Redis;

namespace NewStore.Gateway.Auth;

/// <summary>
/// Keeps the signed-in session (the claims and the Keycloak tokens) in Redis, so the session cookie carries only a key. The
/// cookie handler calls it through <see cref="CookieAuthenticationOptions.SessionStore"/>. Deleting the key ends the session
/// at once, on every gateway instance.
/// </summary>
internal sealed partial class RedisTicketStore(IConnectionMultiplexer redis, TimeProvider time, ILogger<RedisTicketStore> logger) : ITicketStore
{
    private const string KeyPrefix = "newstore:session:";
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromHours(8);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var lifetime = ticket.Properties.ExpiresUtc is { } expires ? expires - time.GetUtcNow() : FallbackLifetime;
        if (lifetime <= TimeSpan.Zero)
        {
            // Already expired: Redis refuses a zero or negative expiry, and a session that is over needs no entry.
            await RemoveAsync(key);
            return;
        }

        await redis.GetDatabase().StringSetAsync(key, TicketSerializer.Default.Serialize(ticket), lifetime);
    }

    /// <summary>A Redis failure is logged and the session counts as absent: the request is anonymous, not a 500.</summary>
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        try
        {
            var bytes = (byte[]?)await redis.GetDatabase().StringGetAsync(key);
            return bytes is null ? null : TicketSerializer.Default.Deserialize(bytes);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogReadFailed(logger, ex);
            return null;
        }
    }

    /// <summary>
    /// A Redis failure is logged and swallowed, so sign-out and the session-expired answer still finish. The ticket then stays until
    /// its Redis expiry, which is the same time the cookie handler stops accepting it.
    /// </summary>
    public async Task RemoveAsync(string key)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogRemoveFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis failed while deleting a session; it stays in Redis until it expires.")]
    private static partial void LogRemoveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis failed while reading a session; the request is treated as anonymous.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
