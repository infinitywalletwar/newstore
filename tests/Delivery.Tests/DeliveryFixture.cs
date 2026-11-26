using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NewStore.Contracts.Orders;
using NewStore.Delivery.Shipments;

namespace NewStore.Delivery.Tests;

/// <summary>
/// Delivery on its own database and the shared Kafka, with a clock the test moves. A class fixture: each test class gets one app,
/// started in <see cref="InitializeAsync"/>. Subclasses (the courier's and the tracking tests) change the role and the settings
/// through the virtual members.
/// </summary>
public class DeliveryFixture(PostgresServer postgres, KafkaServer kafka) : NewStoreAppFactory<Program>, IAsyncLifetime
{
    private string? _clientId;

    /// <summary>
    /// Every "now" of the service. It starts a few 100 ns ticks past a whole microsecond: PostgreSQL keeps microseconds, so a
    /// service that returns an unrounded time answers differently before and after a round trip.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(StartOfClock());

    /// <summary>Every log record the app writes, from the moment it starts.</summary>
    public LogCapture Logs { get; } = new();

    public KafkaServer Kafka => kafka;

    /// <summary>This fixture's database. A second host in the same test (Task 5) uses it too.</summary>
    public string ConnectionString { get; private set; } = "";

    /// <summary>
    /// <c>Messaging:ClientId</c>, which names the consumer groups and the inbox's consumer. Two fixtures in one run must never share
    /// it, and a fixture class can serve several test classes, so every instance gets its own. A second host that must share the
    /// first one's groups overrides it.
    /// </summary>
    public virtual string ClientId => _clientId ??= $"delivery-{GetType().Name}-{Guid.NewGuid():N}";

    /// <summary>Which parts of Delivery run. Api by default: the consumer and the REST API, no courier.</summary>
    protected virtual DeliveryRole Role => DeliveryRole.Api;

    /// <summary>Runs after the fixture's own settings, so a subclass can change any of them (courier times, Redis, tracking).</summary>
    protected virtual void ConfigureSettings(Dictionary<string, string?> settings)
    {
    }

    private static DateTimeOffset StartOfClock()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero).AddTicks(3);
    }

    public async ValueTask InitializeAsync()
    {
        ConnectionString = await postgres.CreateDatabaseAsync("delivery", TestContext.Current.CancellationToken);
        Settings["ConnectionStrings:Delivery"] = ConnectionString;
        Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        Settings["Messaging:ClientId"] = ClientId;
        Settings["Delivery:Role"] = Role.ToString();
        ConfigureSettings(Settings);

        MigrateBeforeStart<DeliveryDbContext>();
        _ = Services; // starts the app now, so its topics exist and its consumer has joined before a test publishes
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(Logs));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>A client that carries a signed-in buyer's token, issued by the gateway for audience <c>delivery</c>.</summary>
    public HttpClient BuyerClient(Guid buyerId) => CreateClient().WithBearer(TestTokens.User(buyerId, "delivery"));

    /// <summary>
    /// Creates a shipment the way Orders' event does: it runs <see cref="ShipmentRequestedHandler"/> on a new event, outside Kafka.
    /// The handler is built here, not resolved, because only the Api role registers it.
    /// </summary>
    public async Task<Shipment> CreateShipmentAsync(Guid? buyerId = null, double latitude = 52.5219, double longitude = 13.4132)
    {
        var request = NewShipmentRequested(buyerId ?? Guid.NewGuid(), latitude, longitude);
        await using (var scope = Services.CreateAsyncScope())
        {
            var handler = new ShipmentRequestedHandler(
                scope.ServiceProvider.GetRequiredService<DeliveryDbContext>(),
                scope.ServiceProvider.GetRequiredService<IOptions<WarehouseOptions>>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>());
            await handler.HandleAsync(request, TestContext.Current.CancellationToken);
        }

        return await QueryAsync(db => db.Shipments.AsNoTracking().SingleAsync(s => s.OrderId == request.OrderId, TestContext.Current.CancellationToken));
    }

    /// <summary>A new event for a new order of <paramref name="buyerId"/>, to Alexanderplatz unless told otherwise.</summary>
    public static ShipmentRequested NewShipmentRequested(Guid buyerId, double latitude = 52.5219, double longitude = 13.4132) => new()
    {
        EventId = Guid.CreateVersion7(),
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = Guid.CreateVersion7(),
        BuyerId = buyerId,
        EditionId = Guid.CreateVersion7(),
        VideoId = Guid.CreateVersion7(),
        CopyNumber = 7,
        Address = "Alexanderplatz 1, 10178 Berlin",
        Latitude = latitude,
        Longitude = longitude,
    };

    /// <summary>Runs <paramref name="query"/> with a database context of the app.</summary>
    public async Task<T> QueryAsync<T>(Func<DeliveryDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<DeliveryDbContext>());
    }

    /// <summary>Polls until <paramref name="condition"/> holds; fails the test after <paramref name="timeout"/> (30 s by default).</summary>
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

/// <summary>A warehouse other than the defaults, so a test can tell configuration from the class's initial values.</summary>
public sealed class OtherWarehouseFixture(PostgresServer postgres, KafkaServer kafka) : DeliveryFixture(postgres, kafka)
{
    public const string Name = "Test depot, Spandau";
    public const double Latitude = 52.5355;
    public const double Longitude = 13.1975;

    protected override void ConfigureSettings(Dictionary<string, string?> settings)
    {
        settings["Delivery:Warehouse:Name"] = Name;
        settings["Delivery:Warehouse:Latitude"] = Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["Delivery:Warehouse:Longitude"] = Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
