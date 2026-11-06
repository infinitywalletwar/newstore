using System.Text.RegularExpressions;

using Npgsql;

using Testcontainers.PostgreSql;

namespace NewStore.TestSupport;

/// <summary>
/// One PostgreSQL container per test assembly. Register it with <c>[assembly: AssemblyFixture(typeof(PostgresServer))]</c>.
/// Each test class asks for its own database with <see cref="CreateDatabaseAsync"/>, so tests that write rows
/// don't collide.
/// </summary>
public sealed partial class PostgresServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6").Build();
    private int _databaseCount;

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Runs <c>CREATE DATABASE "name_n"</c> and returns that database's connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        // The name goes into SQL text, because CREATE DATABASE takes no parameters.
        if (!SafeName().IsMatch(name))
        {
            throw new ArgumentException("Use letters, digits and underscores only.", nameof(name));
        }

        var database = $"{name}_{Interlocked.Increment(ref _databaseCount)}";

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SafeName();
}
