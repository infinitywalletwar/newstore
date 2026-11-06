using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;

using Npgsql;

namespace NewStore.Catalog.Tests;

public class DatabaseCommandTests(PostgresServer postgres)
{
    /// <summary>
    /// WebApplicationFactory can't pass command-line arguments, so build the app the way Program.cs does,
    /// on a fresh database. The host is built but never started.
    /// </summary>
    private async Task<(WebApplication App, string ConnectionString)> BuildAppAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync("commands", TestContext.Current.CancellationToken);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration["ConnectionStrings:Catalog"] = connectionString;
        builder.AddServiceDefaults();
        builder.AddPostgresDatabase<CatalogDbContext>("Catalog");
        return (builder.Build(), connectionString);
    }

    [Fact]
    public async Task Migrate_creates_the_migrations_history_and_returns_0()
    {
        var ct = TestContext.Current.CancellationToken;
        var (app, connectionString) = await BuildAppAsync();
        await using var _ = app;

        var result = await app.RunDatabaseCommandsAsync<CatalogDbContext>(["migrate"]);

        Assert.Equal(0, result);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "__EFMigrationsHistory" """, connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Seed_without_a_seeder_returns_0()
    {
        var (app, _) = await BuildAppAsync();
        await using var _ = app;

        var result = await app.RunDatabaseCommandsAsync<CatalogDbContext>(["seed"]);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Unknown_command_returns_2()
    {
        var (app, _) = await BuildAppAsync();
        await using var _ = app;

        var result = await app.RunDatabaseCommandsAsync<CatalogDbContext>(["no-such-command"]);

        Assert.Equal(2, result);
    }

    [Fact]
    public async Task No_arguments_returns_null()
    {
        var (app, _) = await BuildAppAsync();
        await using var _ = app;

        var result = await app.RunDatabaseCommandsAsync<CatalogDbContext>([]);

        Assert.Null(result);
    }

    [Fact]
    public async Task Options_are_configuration_not_commands()
    {
        var (app, _) = await BuildAppAsync();
        await using var _ = app;

        // WebApplicationFactory starts Program with options like these; they are not commands.
        Assert.Null(await app.RunDatabaseCommandsAsync<CatalogDbContext>(["--environment=Testing", "--contentRoot=/tmp"]));
        Assert.Equal(0, await app.RunDatabaseCommandsAsync<CatalogDbContext>(["--environment=Testing", "seed"]));
    }
}
