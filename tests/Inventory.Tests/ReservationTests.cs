using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The reservation rules, run once per reserve strategy (see the two subclasses below). Both strategies must
/// give the same answers; they differ in how they get there under contention (ADR 0007).
/// </summary>
public abstract class ReservationTests(InventoryFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reserve_takes_one_copy()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        var buyerId = Guid.NewGuid();

        var response = await client.ReserveAsync(edition.Id, buyerId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reservation = (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!;
        Assert.Equal($"/reservations/{reservation.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Pending", reservation.Status);
        Assert.Equal(buyerId, reservation.BuyerId);
        Assert.Null(reservation.CopyNumber);
        Assert.Equal(TimeSpan.FromMinutes(10), reservation.ExpiresAt - reservation.ReservedAt);
        Assert.Equal(9, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task Reserve_when_sold_out_returns_409_sold_out()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 1);
        await client.ReserveOkAsync(edition.Id);

        var response = await client.ReserveAsync(edition.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:sold-out", (await response.ReadProblemAsync()).Type);
        Assert.Equal(0, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task Same_buyer_cannot_hold_two_reservations_at_once()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 100);
        var buyerId = Guid.NewGuid();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => client.ReserveAsync(edition.Id, buyerId)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var conflicts = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(49, conflicts.Count);
        foreach (var conflict in conflicts)
        {
            Assert.Equal("urn:newstore:problem:already-holding", (await conflict.ReadProblemAsync()).Type);
        }

        // The 49 losers rolled their decrement back with the failed insert.
        Assert.Equal(99, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task A_hundred_buyers_for_ten_copies_get_exactly_ten()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => client.ReserveAsync(edition.Id, Guid.NewGuid())));

        // No 503 for either strategy: ConcurrencyToken must get through within its attempts.
        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var conflicts = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(90, conflicts.Count);
        foreach (var conflict in conflicts)
        {
            Assert.Equal("urn:newstore:problem:sold-out", (await conflict.ReadProblemAsync()).Type);
        }

        Assert.Equal(0, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task Reserve_for_unknown_edition_returns_404()
    {
        using var client = fixture.CreateClient();

        var response = await client.ReserveAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("urn:newstore:problem:not-found", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Confirm_assigns_numbers_in_confirm_order_without_gaps()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 5);
        var first = await client.ReserveOkAsync(edition.Id);
        var second = await client.ReserveOkAsync(edition.Id);
        var third = await client.ReserveOkAsync(edition.Id);

        var numbers = new List<int?>();
        foreach (var reservation in new[] { third, first, second })
        {
            var response = await client.ConfirmAsync(reservation.Id);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var confirmed = (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!;
            Assert.Equal("Confirmed", confirmed.Status);
            numbers.Add(confirmed.CopyNumber);
        }

        Assert.Equal<int?>([1, 2, 3], numbers);
        var copies = await client.GetCopiesAsync(edition.Id);
        Assert.Equal([1, 2, 3], copies.Select(c => c.Number));
        Assert.Equal([third.BuyerId, first.BuyerId, second.BuyerId], copies.Select(c => c.OwnerId));
        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesSold);
    }

    [Fact]
    public async Task Confirm_twice_returns_the_same_copy_number()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();
        var reservation = await client.ReserveOkAsync(edition.Id);

        var one = await (await client.ConfirmAsync(reservation.Id)).Content.ReadFromJsonAsync<ReservationDto>(Ct);
        var two = await (await client.ConfirmAsync(reservation.Id)).Content.ReadFromJsonAsync<ReservationDto>(Ct);

        Assert.Equal(1, one!.CopyNumber);
        Assert.Equal(one.CopyNumber, two!.CopyNumber);
        Assert.Single(await client.GetCopiesAsync(edition.Id));
    }

    [Fact]
    public async Task Confirm_after_expiry_fails_and_returns_the_copy()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 3);
        var reservation = await client.ReserveOkAsync(edition.Id);

        // Past the TTL. Confirm decides by the reservation's ExpiresAt, whether or not the sweeper ran yet.
        fixture.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var response = await client.ConfirmAsync(reservation.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-expired", (await response.ReadProblemAsync()).Type);
        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        Assert.Equal("Expired", (await client.GetReservationAsync(reservation.Id)).Status);
    }

    [Fact]
    public async Task Sweeper_expires_pending_reservations_and_returns_their_copies()
    {
        using var client = fixture.CreateClient();
        var busy = await client.CreateEditionAsync(copies: 5);
        var other = await client.CreateEditionAsync(copies: 5);
        var reservations = new[]
        {
            await client.ReserveOkAsync(busy.Id),
            await client.ReserveOkAsync(busy.Id),
            await client.ReserveOkAsync(busy.Id),
            await client.ReserveOkAsync(other.Id),
        };

        fixture.Clock.Advance(TimeSpan.FromMinutes(11));

        await Eventually(async () =>
        {
            foreach (var reservation in reservations)
            {
                Assert.Equal("Expired", (await client.GetReservationAsync(reservation.Id)).Status);
            }

            Assert.Equal(5, (await client.GetEditionAsync(busy.Id)).CopiesLeft);
            Assert.Equal(5, (await client.GetEditionAsync(other.Id)).CopiesLeft);
        });
    }

    [Fact]
    public async Task Expired_reservation_never_uses_a_number()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 5);
        var a = await client.ReserveOkAsync(edition.Id);
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        var b = await client.ReserveOkAsync(edition.Id);
        await Eventually(async () => Assert.Equal("Expired", (await client.GetReservationAsync(a.Id)).Status));

        var confirmed = await (await client.ConfirmAsync(b.Id)).Content.ReadFromJsonAsync<ReservationDto>(Ct);

        Assert.Equal(1, confirmed!.CopyNumber);
    }

    [Fact]
    public async Task Release_returns_the_copy_and_is_idempotent()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 4);
        var reservation = await client.ReserveOkAsync(edition.Id);
        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesLeft);

        Assert.Equal(HttpStatusCode.NoContent, (await client.ReleaseAsync(reservation.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.ReleaseAsync(reservation.Id)).StatusCode);

        Assert.Equal(4, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        Assert.Equal("Released", (await client.GetReservationAsync(reservation.Id)).Status);

        // The buyer may reserve again: the released reservation left the partial unique index.
        var again = await client.ReserveOkAsync(edition.Id, reservation.BuyerId);
        await client.ConfirmAsync(again.Id);
        var response = await client.ReleaseAsync(again.Id);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-confirmed", (await response.ReadProblemAsync()).Type);

        var released = await client.ConfirmAsync(reservation.Id);
        Assert.Equal(HttpStatusCode.Conflict, released.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-released", (await released.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Confirm_unknown_reservation_returns_404()
    {
        using var client = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.ConfirmAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.ReleaseAsync(Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Edit_after_a_reservation_returns_409_edition_locked_even_after_release()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        var reservation = await client.ReserveOkAsync(edition.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.ReleaseAsync(reservation.Id)).StatusCode);

        var response = await client.UpdateEditionAsync(edition.Id, copies: 5);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:edition-locked", (await response.ReadProblemAsync()).Type);
        var after = await client.GetEditionAsync(edition.Id);
        Assert.NotNull(after.LockedAt);
        Assert.Equal(10, after.CopiesTotal);
    }

    [Fact]
    public async Task Reserve_after_the_sale_ended_returns_409_sale_ended()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        await client.EndSaleAsync(edition.Id);

        var response = await client.ReserveAsync(edition.Id, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:sale-ended", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Pending_reservation_survives_the_end_of_the_sale()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        var reservation = await client.ReserveOkAsync(edition.Id);
        await client.EndSaleAsync(edition.Id);

        var response = await client.ConfirmAsync(reservation.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<ReservationDto>(Ct))!.CopyNumber);
        var after = await client.GetEditionAsync(edition.Id);
        Assert.Equal(1, after.CopiesSold);
        Assert.Equal(0, after.CopiesLeft);
    }

    [Fact]
    public async Task Expired_reservation_does_not_return_its_copy_after_the_sale_ended()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 10);
        var reservation = await client.ReserveOkAsync(edition.Id);
        await client.EndSaleAsync(edition.Id);

        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReservationService>()
                .SweepAsync(fixture.Clock.GetUtcNow(), Ct);
        }

        Assert.Equal("Expired", (await client.GetReservationAsync(reservation.Id)).Status);
        Assert.Equal(0, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
    }

    [Fact]
    public async Task Edit_and_reserve_racing_never_leave_the_count_inconsistent()
    {
        using var client = fixture.CreateClient();

        for (var round = 0; round < 20; round++)
        {
            var edition = await client.CreateEditionAsync(copies: 10);
            var reserves = Enumerable.Range(0, 5)
                .Select(_ => Task.Run(() => client.ReserveAsync(edition.Id, Guid.NewGuid()), Ct)).ToList();
            var edit = Task.Run(() => client.UpdateEditionAsync(edition.Id, copies: 7), Ct);
            await Task.WhenAll(reserves.Append(edit));

            var editStatus = (await edit).StatusCode;
            Assert.True(
                editStatus is HttpStatusCode.OK or HttpStatusCode.Conflict, $"round {round}: PATCH answered {editStatus}");

            await using var scope = fixture.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var row = await db.Editions.AsNoTracking().SingleAsync(e => e.Id == edition.Id, Ct);
            var held = await db.Reservations.CountAsync(
                r => r.EditionId == edition.Id
                    && (r.Status == ReservationStatus.Pending || r.Status == ReservationStatus.Confirmed), Ct);
            Assert.Equal(held, row.CopiesTotal - row.CopiesLeft);
            if (held > 0)
            {
                Assert.NotNull(row.LockedAt);
            }
        }
    }

    [Fact]
    public async Task Sweeper_heartbeat_is_live()
    {
        using var client = fixture.CreateClient();

        // A minute is far past the check's limit (3 sweep intervals + 5 s). Only a sweeper that looped
        // after the jump has stamped a fresh heartbeat.
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        await Eventually(async () =>
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode));
    }

    /// <summary>The sweeper runs in the background: poll an assertion for up to 2 seconds.</summary>
    private static async Task Eventually(Func<Task> assertion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Xunit.Sdk.XunitException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            }
        }
    }
}

public sealed class ReservationTests_ConditionalUpdate(ConditionalUpdateFixture fixture)
    : ReservationTests(fixture), IClassFixture<ConditionalUpdateFixture>;

public sealed class ReservationTests_ConcurrencyToken(ConcurrencyTokenFixture fixture)
    : ReservationTests(fixture), IClassFixture<ConcurrencyTokenFixture>;
