using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace NewStore.Inventory.Tests;

public sealed class MigrationTests(PostgresServer postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_locks_editions_that_already_have_reservations()
    {
        var connectionString = await postgres.CreateDatabaseAsync("inventory_migration", Ct);
        await using var context = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
        var migrator = context.GetService<IMigrator>();

        // The database as it was before this change: only the Initial migration applied.
        await migrator.MigrateAsync("20261106220529_Initial", Ct);

        var reserved = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var earlier = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Ct);
            foreach (var id in new[] { reserved, untouched })
            {
                await Execute(connection, """
                    INSERT INTO editions (id, video_id, creator_id, format, price, copies_total, copies_left, copies_assigned, created_at)
                    VALUES (@id, @id, @id, 'Digital', 5, 10, 10, 0, @at)
                    """, ("id", id), ("at", earlier));
            }

            // A Released one first in time and a Pending one later: any status locks, and the earliest wins.
            await Execute(connection, """
                INSERT INTO reservations (id, edition_id, buyer_id, status, reserved_at, expires_at)
                VALUES (@a, @e, @a, 'Pending', @later, @later + interval '10 minutes'),
                       (@b, @e, @b, 'Released', @earlier, @earlier + interval '10 minutes')
                """, ("a", Guid.NewGuid()), ("b", Guid.NewGuid()), ("e", reserved),
                ("earlier", earlier), ("later", earlier.AddHours(1)));
        }

        await migrator.MigrateAsync(null, Ct);

        await using var check = new NpgsqlConnection(connectionString);
        await check.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT id, locked_at FROM editions", check);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lockedAt = new Dictionary<Guid, DateTimeOffset?>();
        while (await reader.ReadAsync(Ct))
        {
            lockedAt[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }

        Assert.Equal(earlier, lockedAt[reserved]);
        Assert.Null(lockedAt[untouched]);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }
}
