using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NewStore.Gateway.Auth;

using StackExchange.Redis;

using Testcontainers.Redis;

namespace NewStore.Gateway.Tests;

public sealed class TicketStoreTests
{
    private static AuthenticationTicket Ticket(DateTimeOffset? expires) =>
        new(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "u")], "test")),
            new AuthenticationProperties { ExpiresUtc = expires }, "cookie");

    [Fact]
    public async Task Removing_a_session_while_redis_is_down_does_not_throw()
    {
        // Nothing listens on port 1. The connection is configured as in the gateway: no abort on connect failure, short timeouts.
        await using var redis = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=500,syncTimeout=500,asyncTimeout=500");
        var store = new RedisTicketStore(redis, TimeProvider.System, NullLogger<RedisTicketStore>.Instance);

        await store.RemoveAsync("newstore:session:gone");
    }

    [Fact]
    public async Task Renewing_a_ticket_that_has_already_expired_stores_nothing_and_does_not_throw()
    {
        await using var container = new RedisBuilder("redis:8.10.3").Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var store = new RedisTicketStore(redis, time, NullLogger<RedisTicketStore>.Instance);

        await store.RenewAsync("newstore:session:old", Ticket(time.GetUtcNow().AddMinutes(-1)));

        Assert.Null(await store.RetrieveAsync("newstore:session:old"));
        // The live case, for contrast: a ticket with time left is stored and read back.
        await store.RenewAsync("newstore:session:live", Ticket(time.GetUtcNow().AddMinutes(5)));
        Assert.NotNull(await store.RetrieveAsync("newstore:session:live"));
    }
}
