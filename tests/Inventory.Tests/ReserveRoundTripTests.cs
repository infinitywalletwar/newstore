using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The statements one reserve sends with the default strategy, ConditionalUpdate. In the last-copy load test 1000 buyers
/// share one 20-connection pool and 990 of them lose, so every statement a losing buyer sends is paid in that queue (#97,
/// ADR 0013, "Amendment, 2026-11"). Each client sends its own trace id, so each test counts only its buyer's statements.
/// </summary>
public sealed class ReserveRoundTripTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>
{
    [Fact]
    public async Task A_sold_out_reserve_sends_two_statements()
    {
        using var setup = fixture.CreateClient();
        var edition = await setup.CreateEditionAsync(copies: 1);
        await setup.ReserveOkAsync(edition.Id);

        using var buyer = fixture.CreateClient();
        var response = await buyer.ReserveAsync(edition.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:sold-out", (await response.ReadProblemAsync()).Type);

        // The reserve statement, which takes no copy; then one read of the edition and of the reservation id together.
        Assert.Equal("WITH, SELECT", StatementsOf(buyer));
    }

    [Fact]
    public async Task A_winning_reserve_sends_two_statements()
    {
        using var setup = fixture.CreateClient();
        var edition = await setup.CreateEditionAsync(copies: 1);

        using var buyer = fixture.CreateClient();
        var response = await buyer.ReserveAsync(edition.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The reserve statement takes the copy and inserts the reservation; the endpoint reads the edition for the body.
        Assert.Equal("WITH, SELECT", StatementsOf(buyer));
    }

    [Fact]
    public async Task A_retry_with_the_same_id_sends_four_statements()
    {
        using var setup = fixture.CreateClient();
        var edition = await setup.CreateEditionAsync(copies: 2);
        var (reservationId, buyerId) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, (await setup.ReserveAsync(edition.Id, buyerId, reservationId)).StatusCode);

        using var retry = fixture.CreateClient();
        var response = await retry.ReserveAsync(edition.Id, buyerId, reservationId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The reserve statement breaks the primary key, so it is undone with its decrement; the lookup finds the reservation;
        // the endpoint reads the copy number and the edition. Only a real retry pays for the lookup.
        Assert.Equal("WITH, SELECT, SELECT, SELECT", StatementsOf(retry));
    }

    [Fact]
    public async Task A_deadlock_on_the_reserve_statement_is_resent_once()
    {
        using var setup = fixture.CreateClient();
        var edition = await setup.CreateEditionAsync(copies: 1);

        using var buyer = fixture.CreateClient();
        fixture.Deadlocks.FailNext(1);
        var response = await buyer.ReserveAsync(edition.Id, Guid.NewGuid());

        // Answers as if nothing had failed, and the statement went out twice: the failed one, then the resend.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("WITH, WITH, SELECT", StatementsOf(buyer));
        Assert.Equal(0, (await setup.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task A_second_deadlock_is_not_resent()
    {
        using var setup = fixture.CreateClient();
        var edition = await setup.CreateEditionAsync(copies: 1);

        using var buyer = fixture.CreateClient();
        fixture.Deadlocks.FailNext(2);
        var response = await buyer.ReserveAsync(edition.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("WITH, WITH", StatementsOf(buyer));
        Assert.Equal(1, (await setup.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task The_capture_counts_synchronous_calls()
    {
        using var activity = new Activity("sync-capture").Start();
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        db.Database.ExecuteSqlRaw("SELECT 1");
        using (db.Database.BeginTransaction())
        {
        }

        Assert.Equal(["SELECT 1", "BEGIN"], fixture.Statements.Of(activity.TraceId));
    }

    /// <summary>
    /// The first word of each statement the client's requests sent, in order and joined, so a failure shows the whole sequence.
    /// The first word is enough to tell the reserve statement from a read.
    /// </summary>
    private string StatementsOf(HttpClient client) =>
        string.Join(", ", fixture.Statements.Of(client).Select(sql => sql.TrimStart().Split([' ', '\n', '\r'], 2)[0]));
}
