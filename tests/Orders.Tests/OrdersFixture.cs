using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

using NewStore.Messaging.Outbox;
using NewStore.Orders.Clients;
using NewStore.Orders.Orders;

namespace NewStore.Orders.Tests;

/// <summary>
/// Orders on its own database and the shared Kafka, with stubs where Inventory and Payments would be. One app for all test classes
/// (<see cref="OrdersCollectionDefinition"/>): every app subscribes to <c>payments.payments</c> in one consumer group, so a second app
/// would take some of the first one's messages. Tests use new ids each, so they do not collide, and they run one after another.
/// </summary>
public sealed class OrdersFixture(PostgresServer postgres, KafkaServer kafka) : NewStoreAppFactory<Program>, IAsyncLifetime
{
    internal InventoryStub Inventory { get; } = new();

    internal PaymentsStub Payments { get; } = new();

    public KafkaServer Kafka => kafka;

    public async ValueTask InitializeAsync()
    {
        Settings["ConnectionStrings:Orders"] = await postgres.CreateDatabaseAsync("orders", TestContext.Current.CancellationToken);
        Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        Settings["Messaging:RelayInterval"] = "00:00:00.100";
        // Retries after a transient failure start quickly, so a test about them does not take minutes.
        Settings["Messaging:TransientRetryPause"] = "00:00:00.050";
        Settings["Messaging:MaxTransientRetryPause"] = "00:00:00.200";
        // One attempt for a failure that is not transient. A transient failure ignores the limit, so a test that keeps Inventory down
        // for many handler runs shows that the failure is classified as transient: otherwise the message would be dead-lettered.
        Settings["Messaging:MaxHandlerAttempts"] = "1";
        Settings["Auth:ClientId"] = "orders";
        Settings["Auth:ClientSecret"] = "orders";
        Settings["Auth:TokenEndpoint"] = "http://keycloak.test/token";
        Settings["Services:Inventory"] = "http://inventory.test";
        Settings["Services:Payments"] = "http://payments.test";
        Settings["Orders:WebBaseUrl"] = "http://shop.test";
        await MigrateAsync<OrdersDbContext>();
    }

    /// <summary>Every test starts with the stubs' normal answers.</summary>
    public void Reset()
    {
        Inventory.Reset();
        Payments.Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // The stubs go below the resilience and token handlers, so a test sees every request on the wire, retries included.
            services.UseStubHandler<InventoryClient>(Inventory.Handler);
            services.UseStubHandler<PaymentsClient>(Payments.Handler);

            // One retry and no wait: a service that is down fails a call quickly, and the consumer's own retry is what the test sees.
            foreach (var client in new[] { nameof(InventoryClient), nameof(PaymentsClient) })
            {
                services.Configure<HttpStandardResilienceOptions>(client + "-standard", o =>
                {
                    o.Retry.MaxRetryAttempts = 1;
                    o.Retry.Delay = TimeSpan.Zero;
                    o.Retry.UseJitter = false;
                });
            }

            // Every "now" ends in a few 100 ns ticks. PostgreSQL keeps microseconds, so an order that stored or returned an
            // unrounded time would show it. Timers and delays still use the system clock.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new SubMicrosecondClock());
        });
    }

    public HttpClient BuyerClient(Guid buyerId) => CreateClient().WithBearer(TestTokens.User(buyerId, "orders"));

    /// <summary>Buys through the endpoint, as the buyer: the order, still <c>Reserved</c>.</summary>
    public async Task<OrderResponse> BuyAsync(Guid buyerId, Guid? editionId = null)
    {
        using var client = BuyerClient(buyerId);
        var response = await client.PostAsJsonAsync("/orders", new { editionId = editionId ?? Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Buys and pays: the order is <c>Paying</c> and has a payment.</summary>
    public async Task<Order> PayingOrderAsync(Guid? buyerId = null)
    {
        var buyer = buyerId ?? Guid.NewGuid();
        var order = await BuyAsync(buyer);
        using var client = BuyerClient(buyer);
        var response = await client.PostAsync($"/orders/{order.Id}/pay", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await GetOrderAsync(order.Id);
    }

    public async Task<T> QueryAsync<T>(Func<OrdersDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<OrdersDbContext>());
    }

    public Task<Order> GetOrderAsync(Guid orderId) =>
        QueryAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, TestContext.Current.CancellationToken));

    public Task<int> CountOutboxAsync<TEvent>(Guid orderId) =>
        QueryAsync(db => db.Set<OutboxMessage>().CountAsync(
            m => m.Type == typeof(TEvent).FullName && m.Key == orderId.ToString(), TestContext.Current.CancellationToken));

    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after <paramref name="timeout"/>.</summary>
    public static async Task WaitAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for: {what}.");
    }

    public Task WaitForStatusAsync(Guid orderId, OrderStatus status) =>
        WaitAsync(async () => (await GetOrderAsync(orderId)).Status == status, $"order {orderId} is {status}");
}

/// <summary>The system clock with the sub-microsecond part of the time set to 300 ns.</summary>
internal sealed class SubMicrosecondClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow()
    {
        var now = base.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10) + 3, TimeSpan.Zero);
    }
}

[CollectionDefinition("orders")]
public sealed class OrdersCollectionDefinition : ICollectionFixture<OrdersFixture>;
