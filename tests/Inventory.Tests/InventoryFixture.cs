using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Inventory on its own database, with a clock the test moves. A class fixture: each test class gets
/// one app, one database and one <see cref="Clock"/>. Subclasses choose the reserve strategy.
/// </summary>
public abstract class InventoryFixture(PostgresServer postgres, KafkaServer kafka, ReserveStrategy strategy)
    : NewStoreAppFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Every "now" of the service, including the sweeper's timer. It starts a few 100 ns ticks past a whole microsecond: PostgreSQL
    /// keeps microseconds, so a service that stores or returns an unrounded time answers differently before and after a round trip.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(WholeMicrosecond(DateTimeOffset.UtcNow).AddTicks(3));

    private static DateTimeOffset WholeMicrosecond(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>Every log record the app writes, whatever the configured log levels.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>Every statement the app's <see cref="InventoryDbContext"/> sends, with the trace of the request that sent it.</summary>
    public StatementCapture Statements { get; } = new();

    /// <summary>How often the background sweeper ticks on <see cref="Clock"/>. A subclass sets a long one to keep the sweeper out.</summary>
    protected virtual string SweepInterval => "00:00:01";

    public async ValueTask InitializeAsync()
    {
        Settings["ConnectionStrings:Inventory"] =
            await postgres.CreateDatabaseAsync("inventory", TestContext.Current.CancellationToken);
        Settings["Messaging:BootstrapServers"] = kafka.BootstrapServers;
        Settings["Inventory:ReserveStrategy"] = strategy.ToString();
        Settings["Inventory:ExpirySweepInterval"] = SweepInterval;
        await MigrateAsync<InventoryDbContext>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddLogging(logging => logging.AddProvider(Logs));

            // Composed with the app's own AddDbContext configuration (EF Core 9 and later).
            services.ConfigureDbContext<InventoryDbContext>(options => options.AddInterceptors(Statements));
        });
    }
}

public sealed class ConditionalUpdateFixture(PostgresServer postgres, KafkaServer kafka)
    : InventoryFixture(postgres, kafka, ReserveStrategy.ConditionalUpdate);

public sealed class ConcurrencyTokenFixture(PostgresServer postgres, KafkaServer kafka)
    : InventoryFixture(postgres, kafka, ReserveStrategy.ConcurrencyToken);

/// <summary>
/// The background sweeper ticks once a day on the fake clock, so it never runs inside a test: only the code under test
/// (hold, confirm) can expire a reservation, and a test that wants a sweep calls it.
/// </summary>
public sealed class NoSweepFixture(PostgresServer postgres, KafkaServer kafka)
    : InventoryFixture(postgres, kafka, ReserveStrategy.ConditionalUpdate)
{
    protected override string SweepInterval => "1.00:00:00";
}
