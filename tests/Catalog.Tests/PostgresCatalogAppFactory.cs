namespace NewStore.Catalog.Tests;

/// <summary>
/// Catalog wired to its own database on the assembly's PostgreSQL container. A class fixture: each test
/// class that asks for it gets one app and one database.
/// </summary>
public sealed class PostgresCatalogAppFactory(PostgresServer postgres)
    : NewStoreAppFactory<Program>, IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        Settings["ConnectionStrings:Catalog"] =
            await postgres.CreateDatabaseAsync("catalog", TestContext.Current.CancellationToken);
}
