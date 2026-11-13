using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

using NewStore.Messaging.Outbox;
using NewStore.Payments.Payments;
using NewStore.Payments.Provider;

namespace NewStore.Payments.Tests;

/// <summary>
/// Payments on its own database and the shared Kafka, with a stub where FakePay would be. One app for all test classes
/// (<see cref="PaymentsCollectionDefinition"/>): every app subscribes to <c>orders.orders</c> and would also see the other tests' refund
/// events. Tests use a new order id each, so they do not collide.
/// </summary>
public sealed class PaymentsFixture(PostgresServer postgres, KafkaServer kafka) : NewStoreAppFactory<Program>, IAsyncLifetime
{
    internal FakePayStub Provider { get; } = new();

    /// <summary>Every log record the app writes.</summary>
    public LogCapture Logs { get; } = new();

    public KafkaServer Kafka => kafka;

    public async ValueTask InitializeAsync()
    {
        Settings["ConnectionStrings:Payments"] = await postgres.CreateDatabaseAsync("payments", TestContext.Current.CancellationToken);
        Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        Settings["Messaging:RelayInterval"] = "00:00:00.100";
        // Retries after a transient failure start quickly, so a test about them does not take minutes.
        Settings["Messaging:TransientRetryPause"] = "00:00:00.050";
        Settings["Messaging:MaxTransientRetryPause"] = "00:00:00.200";
        // One handler attempt for a failure that is not transient. A transient failure ignores this limit, so a test that keeps the
        // provider down for several handler runs shows that the failure is classified as transient: if it were not, the message
        // would be dead-lettered after the first run.
        Settings["Messaging:MaxHandlerAttempts"] = "1";
        Settings["Payments:Provider:BaseUrl"] = "http://fakepay.test";
        Settings["Payments:Provider:ApiKey"] = "test-key";
        Settings["Payments:Provider:WebhookSecret"] = TestWebhooks.Secret;
        await MigrateAsync<PaymentsDbContext>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(Logs));

            // The stub goes below both of the provider's clients (the refund client is named after the interface).
            services.AddHttpClient(nameof(IPaymentProvider)).ConfigurePrimaryHttpMessageHandler(() => Provider.Primary);
            services.AddHttpClient(FakePayProvider.SessionClientName).ConfigurePrimaryHttpMessageHandler(() => Provider.Primary);

            // Only the refund client's pipeline is trimmed: one retry and no wait, so a provider that is down fails the handler
            // quickly and the consumer's own retry is what the test sees. The session client keeps the options the service ships.
            services.Configure<HttpStandardResilienceOptions>(nameof(IPaymentProvider) + "-standard", o =>
            {
                o.Retry.MaxRetryAttempts = 1;
                o.Retry.Delay = TimeSpan.Zero;
                o.Retry.UseJitter = false;
            });
        });
    }

    /// <summary>A client that carries Orders' token, the only caller the payment endpoints accept.</summary>
    public HttpClient CreateOrdersClient() => CreateClient().WithBearer(TestTokens.Client("orders", "payments"));

    public async Task<PaymentResponse> CreatePaymentAsync(Guid? orderId = null, decimal amount = 9.99m)
    {
        using var client = CreateOrdersClient();
        var response = await client.PostAsJsonAsync(
            "/payments", new { orderId = orderId ?? Guid.NewGuid(), amount, returnUrl = "http://shop.test/orders" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>(TestContext.Current.CancellationToken))!;
    }

    public async Task<T> QueryAsync<T>(Func<PaymentsDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public Task<PaymentAttempt> GetAttemptAsync(Guid paymentId) =>
        QueryAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == paymentId, TestContext.Current.CancellationToken));

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
}

[CollectionDefinition("payments")]
public sealed class PaymentsCollectionDefinition : ICollectionFixture<PaymentsFixture>;
