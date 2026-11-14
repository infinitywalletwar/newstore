using System.Net.Http.Json;

using Npgsql;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The real deadlock behind the resend in <see cref="ConditionalUpdateStrategy"/> (#97): a retry of reservation X while the
/// sweep is expiring X. The sweep's order is played by a second connection, as <see cref="LockOrderTests"/> plays confirm and
/// the sweep. The sweeper itself stays out (a one-day interval in <see cref="ReserveDeadlockFixture"/>), so the test controls the order.
/// </summary>
public sealed class ReserveDeadlockTests(ReserveDeadlockFixture fixture) : IClassFixture<ReserveDeadlockFixture>
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
        // Fails the sweep's UPDATE fast, and with a clear message, when it waited longer than the budget allows (see below).
        await ExecuteAsync(sweep, "SET lock_timeout = '20s'");

        // The sweep's first step: the reservation row, replaced by an Expired version that is not committed yet.
        await using var transaction = await sweep.BeginTransactionAsync(Ct);
        await ExecuteAsync(sweep, "UPDATE reservations SET status = 'Expired' WHERE id = @id", ("id", reservationId));

        // The retry takes the edition row, then waits on the primary key for the sweep's uncommitted version.
        using var retryClient = fixture.CreateClient();
        var retry = Task.Run(() => retryClient.ReserveAsync(edition.Id, buyerId, reservationId), Ct);
        await WaitUntilBlockedAsync(observer);

        // Which transaction PostgreSQL aborts "is difficult to predict and should not be relied upon"
        // (https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-DEADLOCKS), so the test steers it. A backend
        // checks for a deadlock once, deadlock_timeout after it starts waiting (ProcSleep arms the timer once, proc.c L1388-L1403),
        // and the one that checks while the cycle exists is the victim. The retry waits first, so it is the victim as long as the
        // sweep's UPDATE below starts waiting within RetryDeadlockTimeout (5 s) of the retry: a timing budget, not a guarantee.
        // The sweep's own check is at 30 s, and its lock_timeout (20 s) is under the retry's 30 s command timeout.
        //
        // The resend is held until the sweep owns the edition row. Without that, once the retry is aborted the resend can reach
        // the edition row before the woken sweep (heap_update sees an aborted xmax and does not queue; the sweep restarts its
        // wait), which makes a second cycle with the resend as victim, and its 40P01 is a 500. That can happen in production too;
        // the gate keeps this test to the single-cycle case.
        using var hold = fixture.Gate.HoldNextReserveStatement();

        // The sweep's second step: the edition row, which the retry holds. Now each waits for the other.
        try
        {
            await ExecuteAsync(sweep, "UPDATE editions SET copies_left = copies_left + 1 WHERE id = @id", ("id", edition.Id));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            Assert.Fail("Timing budget exceeded: the retry was not aborted in time. The sweep's UPDATE started waiting more than " +
                $"{ReserveDeadlockFixture.RetryDeadlockTimeout.TotalSeconds:F0} s after the retry, so the retry's one deadlock check had " +
                "already run without a cycle. A stalled machine, not a broken resend.");
        }

        // The sweep owns the edition row now. The resend usually has not been sent yet (the sweep's backend wakes first), so wait for
        // it to arrive at the gate, then let it go: either way it starts after the sweep owns the row, and queues behind its commit.
        await WaitUntilAsync(() => fixture.Gate.Held == 1, "The retry's resend never reached the gate.");
        hold.Dispose();
        await transaction.CommitAsync(Ct);

        var response = await retry;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Expired", (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!.Status);

        // The gate held the resend: it waited until the sweep owned the edition row. A gate that never holds fails here.
        Assert.Equal(1, fixture.Gate.Held);
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

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(condition(), failure);
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
