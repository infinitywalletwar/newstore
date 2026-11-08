using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NewStore.ServiceDefaults.HealthChecks;

using Npgsql;

namespace NewStore.ServiceDefaults.Data;

public static class PostgresExtensions
{
    // The request pool's size when the connection string does not set one. Each service's budget is
    // 20 request connections + 2 readiness connections (ADR 0002).
    private const int DefaultMaxPoolSize = 20;

    private const string MaxPoolSizeKeyword = "Maximum Pool Size";

    /// <summary>
    /// Registers the service's database: one <see cref="NpgsqlDataSource"/> (request pool of at most 20
    /// connections unless the connection string sets its own), the "postgres" readiness check on its own
    /// 2-connection pool, and <typeparamref name="TContext"/> on that data source with snake_case names.
    /// Throws when ConnectionStrings:<paramref name="connectionName"/> is missing or empty, so a service
    /// without its database fails at startup, not on every request. <paramref name="configure"/> adds
    /// service-specific options to the context, such as a logger factory.
    /// </summary>
    public static IHostApplicationBuilder AddPostgresDatabase<TContext>(
        this IHostApplicationBuilder builder, string connectionName,
        Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string 'ConnectionStrings:{connectionName}' is missing.");
        }

        var requestPool = new NpgsqlConnectionStringBuilder(connectionString);
        // ContainsKey is true for every known keyword; ShouldSerialize is true only for keywords that were set.
        if (!requestPool.ShouldSerialize(MaxPoolSizeKeyword))
        {
            requestPool.MaxPoolSize = DefaultMaxPoolSize;
        }

        builder.Services.AddNpgsqlDataSource(requestPool.ConnectionString);
        builder.Services.AddHealthChecks().AddPostgres(connectionString);
        builder.Services.AddDbContext<TContext>((services, options) =>
        {
            options
                .UseNpgsql(services.GetRequiredService<NpgsqlDataSource>())
                .UseSnakeCaseNamingConvention();
            configure?.Invoke(services, options);
        });

        return builder;
    }

    /// <summary>
    /// Handles the service's command line. Returns null when <paramref name="args"/> holds no command: the
    /// host should run. Otherwise runs each command in order and returns the exit code, and the host never
    /// starts. Arguments that start with '-' or '/' are configuration, which
    /// <c>WebApplication.CreateBuilder(args)</c> reads (WebApplicationFactory passes
    /// <c>--environment=Testing</c> and more), so they are not commands. "migrate" applies pending migrations; "seed" calls <see cref="IDatabaseSeeder{TContext}"/>
    /// from DI (0 and a log line when none is registered). An unknown argument is logged and returns 2.
    /// </summary>
    public static async Task<int?> RunDatabaseCommandsAsync<TContext>(this WebApplication app, string[] args)
        where TContext : DbContext
    {
        string[] commands = [.. args.Where(arg => !IsOption(arg))];
        if (commands.Length == 0)
        {
            return null;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("NewStore.ServiceDefaults.Data.DatabaseCommands");

        foreach (var command in commands)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();

            switch (command)
            {
                case "migrate":
                    DatabaseCommandLog.Migrating(logger, typeof(TContext).Name);
                    await db.Database.MigrateAsync();
                    break;

                case "seed":
                    if (scope.ServiceProvider.GetService<IDatabaseSeeder<TContext>>() is { } seeder)
                    {
                        DatabaseCommandLog.Seeding(logger, typeof(TContext).Name);
                        await seeder.SeedAsync(db, CancellationToken.None);
                    }
                    else
                    {
                        DatabaseCommandLog.NoSeeder(logger, typeof(TContext).Name);
                    }

                    break;

                default:
                    DatabaseCommandLog.UnknownCommand(logger, command);
                    return 2;
            }
        }

        return 0;
    }

    private static bool IsOption(string arg) => arg.StartsWith('-') || arg.StartsWith('/');
}
