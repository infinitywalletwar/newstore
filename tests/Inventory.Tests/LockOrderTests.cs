using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NewStore.Inventory.Tests;

/// <summary>
/// Evidence for the lock order in <see cref="ReservationService"/>: a confirm of an expired, unswept
/// reservation and the sweep take the reservation row first and the edition row second, so they cannot deadlock.
/// </summary>
public sealed class LockOrderTests(ConditionalUpdateFixture fixture) : IClassFixture<ConditionalUpdateFixture>
{
    private const int Copies = 50;
    private const int Sweeps = 5;
    private const int Rounds = 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Confirm_of_an_expired_reservation_and_the_sweep_never_deadlock()
    {
        using var client = fixture.CreateClient();

        for (var round = 0; round < Rounds; round++)
        {
            var edition = await client.CreateEditionAsync(Copies);
            var reservationIds = new List<Guid>();
            for (var i = 0; i < Copies; i++)
            {
                reservationIds.Add((await client.ReserveOkAsync(edition.Id)).Id);
            }

            // Past the TTL, and nobody has swept yet (the test calls SweepAsync itself).
            fixture.Clock.Advance(TimeSpan.FromMinutes(11));
            var now = fixture.Clock.GetUtcNowToMicrosecond();

            // A deadlock would surface here as a PostgresException with SqlState 40P01.
            var confirms = reservationIds.Select(id => Task.Run(async () =>
            {
                await using var scope = fixture.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ReservationService>().ConfirmAsync(id, Ct);
            }, Ct));
            var sweeps = Enumerable.Range(0, Sweeps).Select(_ => Task.Run(async () =>
            {
                await using var scope = fixture.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<ReservationService>().SweepAsync(now, Ct);
            }, Ct));
            var confirmTask = Task.WhenAll(confirms);
            await Task.WhenAll(confirmTask, Task.WhenAll(sweeps));

            Assert.All(await confirmTask, outcome => Assert.IsType<ConfirmOutcome.Expired>(outcome));

            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var states = await db.Reservations.Where(r => r.EditionId == edition.Id).Select(r => r.Status).ToListAsync(Ct);
            Assert.Equal(Copies, states.Count);
            Assert.All(states, s => Assert.Equal(ReservationStatus.Expired, s));
            var row = await db.Editions.AsNoTracking().SingleAsync(e => e.Id == edition.Id, Ct);
            Assert.Equal(Copies, row.CopiesLeft);
            Assert.Equal(0, row.CopiesAssigned);
        }
    }
}
