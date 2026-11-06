using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Inventory on its own database, with a clock the test moves. A class fixture: each test class gets
/// one app, one database and one <see cref="Clock"/>. Subclasses choose the reserve strategy.
/// </summary>
public abstract class InventoryFixture(PostgresServer postgres, ReserveStrategy strategy)
    : NewStoreAppFactory<Program>, IAsyncLifetime
{
    /// <summary>Every "now" of the service, including the sweeper's timer.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public async ValueTask InitializeAsync()
    {
        Settings["ConnectionStrings:Inventory"] =
            await postgres.CreateDatabaseAsync("inventory", TestContext.Current.CancellationToken);
        Settings["Inventory:ReserveStrategy"] = strategy.ToString();
        Settings["Inventory:ExpirySweepInterval"] = "00:00:01";
        await MigrateAsync<InventoryDbContext>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

public sealed class ConditionalUpdateFixture(PostgresServer postgres)
    : InventoryFixture(postgres, ReserveStrategy.ConditionalUpdate);

public sealed class ConcurrencyTokenFixture(PostgresServer postgres)
    : InventoryFixture(postgres, ReserveStrategy.ConcurrencyToken);
