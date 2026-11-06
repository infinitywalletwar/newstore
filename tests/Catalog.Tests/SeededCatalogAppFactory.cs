using Microsoft.Extensions.DependencyInjection;

using NewStore.ServiceDefaults.Data;

namespace NewStore.Catalog.Tests;

/// <summary>
/// Catalog on its own database with the sample catalog (6 channels, 48 videos) already in it. A subclass
/// can add settings in <see cref="ConfigureSettingsAsync"/>, which runs before the app first starts.
/// </summary>
public class SeededCatalogAppFactory(PostgresServer postgres) : NewStoreAppFactory<Program>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Settings["ConnectionStrings:Catalog"] = await postgres.CreateDatabaseAsync("catalog", ct);
        await ConfigureSettingsAsync();

        await MigrateAsync<CatalogDbContext>();
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseSeeder<CatalogDbContext>>()
            .SeedAsync(scope.ServiceProvider.GetRequiredService<CatalogDbContext>(), ct);
    }

    protected virtual ValueTask ConfigureSettingsAsync() => ValueTask.CompletedTask;
}
