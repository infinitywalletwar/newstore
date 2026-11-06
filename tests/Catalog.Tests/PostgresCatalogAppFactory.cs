using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(NewStore.Catalog.Tests.PostgresCatalogAppFactory))]

namespace NewStore.Catalog.Tests;

/// <summary>
/// Catalog wired to a real PostgreSQL container. An assembly fixture: one container and one app
/// serve every test class that asks for it.
/// </summary>
public sealed class PostgresCatalogAppFactory : CatalogAppFactory, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6").Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);
        Settings["ConnectionStrings:Catalog"] = _postgres.GetConnectionString();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
