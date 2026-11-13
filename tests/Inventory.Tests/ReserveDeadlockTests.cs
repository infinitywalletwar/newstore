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
        await ExecuteAsync(sweep, "SET deadlock_timeout = '30s'");

        // The sweep's first step: the reservation row, replaced by an Expired version that is not committed yet.
        await using var transaction = await sweep.BeginTransactionAsync(Ct);
        await ExecuteAsync(sweep, "UPDATE reservations SET status = 'Expired' WHERE id = @id", ("id", reservationId));

        // The retry takes the edition row, then waits on the primary key for the sweep's uncommitted version.
        using var retryClient = fixture.CreateClient();
        var retry = Task.Run(() => retryClient.ReserveAsync(edition.Id, buyerId, reservationId), Ct);
        await WaitUntilBlockedAsync(observer);

        // Which transaction PostgreSQL aborts "is difficult to predict and should not be relied upon"
        // (https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-DEADLOCKS), so the test fixes it. A backend
        // checks for a deadlock once, deadlock_timeout after it starts waiting (ProcSleep in proc.c arms the timer once), and
        // the one that checks first while the cycle exists is the victim. The retry waits first. If the sweep's UPDATE below
        // started waiting after the retry's check (a slow machine), the retry's check found no cycle and the sweep's later check
        // aborted the sweep. So: the retry's connections use NoSweepFixture.RetryDeadlockTimeout (5 s), longer than the sweep needs
        // to reach its UPDATE, and the sweep's session never checks within the test (30 s). The victim is always the retry.
        // The retry's resend is held back until the sweep owns the edition row (below). Otherwise, when PostgreSQL has aborted the
        // retry, the resend could reach the edition row before the sweep's backend, which had been waiting, wakes up (a loaded
        // machine): a second cycle, in which the resend is the victim again, and its 40P01 propagates as a 500.
        fixture.Gate.HoldSecondStatement();
        // The sweep's second step: the edition row, which the retry holds. Now each waits for the other.
        await ExecuteAsync(sweep, "UPDATE editions SET copies_left = copies_left + 1 WHERE id = @id", ("id", edition.Id));
        fixture.Gate.Release();
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

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
