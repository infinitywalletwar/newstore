namespace NewStore.Catalog.Tests;

// The xUnit shared-context page documents assembly fixtures only on test classes. The runner resolves a class
// fixture's constructor arguments through the parent mapping manager, so PostgresServer arrives here (xunit.v3 4.0.2):
// https://github.com/xunit/xunit/blob/main/src/xunit.v3.core/Utility/FixtureMappingManager_reflection.cs
/// <summary>
/// Catalog wired to its own database on the assembly's PostgreSQL container. A class fixture: each test
/// class that asks for it gets one app and one database.
/// </summary>
public sealed class PostgresCatalogAppFactory(PostgresServer postgres, KafkaServer kafka)
    : NewStoreAppFactory<Program>, IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        Settings["ConnectionStrings:Catalog"] =
            await postgres.CreateDatabaseAsync("catalog", TestContext.Current.CancellationToken);
        this.UseKafka(kafka);
    }
}
