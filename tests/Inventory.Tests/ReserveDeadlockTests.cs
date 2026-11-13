using System.Net.Http.Json;

using Npgsql;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The real deadlock behind the resend in <see cref="ConditionalUpdateStrategy"/> (#97): a retry of reservation X while the
/// sweep is expiring X. The sweep's order is played by a second connection, as <see cref="LockOrderTests"/> plays confirm and
/// the sweep. The sweeper itself stays out (<see cref="NoSweepFixture"/>), so the test controls the order.
/// </summary>
public sealed class ReserveDeadlockTests(NoSweepFixture fixture) : IClassFixture<NoSweepFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_retry_that_deadlocks_with_the_sweep_is_resent_and_answers_the_expired_reservation()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 2);
        var (reservationId, buyerId) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, (await client.ReserveAsync(edition.Id, buyerId, reservationId)).StatusCode);

        var connectionString = fixture.Settings["ConnectionStrings:Inventory"]!;
        await using var sweep = new NpgsqlConnection(connectionString);
        await using var observer = new NpgsqlConnection(connectionString);
        await sweep.OpenAsync(Ct);
        await observer.OpenAsync(Ct);

        // The sweep's first step: the reservation row, replaced by an Expired version that is not committed yet.
        await using var transaction = await sweep.BeginTransactionAsync(Ct);
        await ExecuteAsync(sweep, "UPDATE reservations SET status = 'Expired' WHERE id = @id", ("id", reservationId));

        // The retry takes the edition row, then waits on the primary key for the sweep's uncommitted version.
        using var retryClient = fixture.CreateClient();
        var retry = Task.Run(() => retryClient.ReserveAsync(edition.Id, buyerId, reservationId), Ct);
        await WaitUntilBlockedAsync(observer);

        // The sweep's second step: the edition row, which the retry holds. Now each waits for the other, and after
        // deadlock_timeout (1 s by default) PostgreSQL aborts one of them. The retry started waiting first, so its check runs first.
        await ExecuteAsync(sweep, "UPDATE editions SET copies_left = copies_left + 1 WHERE id = @id", ("id", edition.Id));
        await transaction.CommitAsync(Ct);

        var response = await retry;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Expired", (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!.Status);

        // The statement went out twice: the first was the deadlock victim, the resend found the committed Expired row.
        Assert.StartsWith("WITH, WITH", string.Join(", ", fixture.Statements.Of(retryClient)
            .Select(sql => sql.TrimStart().Split([' ', '\n', '\r'], 2)[0])));
        // 2 copies, one reserved, one returned by the sweep; the resend's decrement was undone with its failed statement.
        Assert.Equal(2, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, (string Name, object Value) parameter)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Waits until a backend running the reserve statement waits on a lock.</summary>
    private static async Task WaitUntilBlockedAsync(NpgsqlConnection observer)
    {
        for (var i = 0; i < 100; i++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%INSERT INTO reservations%'", observer);
            if ((long)(await command.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The reserve statement never blocked on a lock.");
    }
}
