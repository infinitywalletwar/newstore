using System.Net.Http.Json;

using Microsoft.Extensions.DependencyInjection;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The hold: a Pending reservation becomes Held while the buyer pays, and Inventory alone decides when the hold ends
/// (45 minutes after it began). The fixture's background sweeper ticks once a day on the fake clock, so it never runs here:
/// a reservation expires only when hold or confirm expires it, or when a test calls the sweep itself. The clock is shared
/// by the class, so each test works on its own edition and moves time forward only (the tests move it far less than a day).
/// </summary>
public sealed class HoldTests(NoSweepFixture fixture) : IClassFixture<NoSweepFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hold_keeps_the_copy_past_ten_minutes()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 3);
        var reservation = await client.ReserveOkAsync(edition.Id);
        var held = await client.HoldOkAsync(reservation.Id);
        Assert.Equal("Held", held.Status);

        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await SweepAsync();

        Assert.Equal("Held", (await client.GetReservationAsync(reservation.Id)).Status);
        Assert.Equal(2, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        var confirm = await client.ConfirmAsync(reservation.Id);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(1, (await confirm.Content.ReadFromJsonAsync<ReservationDto>(Ct))!.CopyNumber);
    }

    [Fact]
    public async Task Held_reservation_expires_at_the_hard_limit()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 3);
        var reservation = await client.ReserveOkAsync(edition.Id);
        var held = await client.HoldOkAsync(reservation.Id);
        // The clock has not moved since the reserve, so the hold began at ReservedAt.
        Assert.Equal(TimeSpan.FromMinutes(45), held.ExpiresAt - reservation.ReservedAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(1));
        await SweepAsync();

        Assert.Equal("Expired", (await client.GetReservationAsync(reservation.Id)).Status);
        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        var confirm = await client.ConfirmAsync(reservation.Id);
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-expired", (await confirm.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Confirm_of_a_held_reservation_past_the_hard_limit_expires_it_without_the_sweep()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 3);
        var reservation = await client.ReserveOkAsync(edition.Id);
        await client.HoldOkAsync(reservation.Id);

        fixture.Clock.Advance(TimeSpan.FromMinutes(45) + TimeSpan.FromSeconds(1));
        var response = await client.ConfirmAsync(reservation.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-expired", (await response.ReadProblemAsync()).Type);
        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        Assert.Equal("Expired", (await client.GetReservationAsync(reservation.Id)).Status);
    }

    [Fact]
    public async Task Hold_twice_does_not_extend()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();
        var reservation = await client.ReserveOkAsync(edition.Id);
        var first = await client.HoldOkAsync(reservation.Id);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await client.HoldOkAsync(reservation.Id);

        Assert.Equal(first.ExpiresAt, second.ExpiresAt);
        Assert.Equal("Held", second.Status);
    }

    [Fact]
    public async Task Hold_after_ten_minutes_is_409_expired()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 3);
        var reservation = await client.ReserveOkAsync(edition.Id);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var response = await client.HoldAsync(reservation.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-expired", (await response.ReadProblemAsync()).Type);
        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        Assert.Equal("Expired", (await client.GetReservationAsync(reservation.Id)).Status);
    }

    [Fact]
    public async Task Hold_of_a_released_reservation_is_409_released()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();
        var reservation = await client.ReserveOkAsync(edition.Id);
        await client.ReleaseAsync(reservation.Id);

        var response = await client.HoldAsync(reservation.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:reservation-released", (await response.ReadProblemAsync()).Type);
    }

    [Fact]
    public async Task Hold_of_an_unknown_reservation_is_404()
    {
        using var client = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.HoldAsync(Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Release_of_a_held_reservation_returns_the_copy()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync(copies: 3);
        var reservation = await client.ReserveOkAsync(edition.Id);
        await client.HoldOkAsync(reservation.Id);
        Assert.Equal(2, (await client.GetEditionAsync(edition.Id)).CopiesLeft);

        Assert.Equal(HttpStatusCode.NoContent, (await client.ReleaseAsync(reservation.Id)).StatusCode);

        Assert.Equal(3, (await client.GetEditionAsync(edition.Id)).CopiesLeft);
        Assert.Equal("Released", (await client.GetReservationAsync(reservation.Id)).Status);
    }

    [Fact]
    public async Task Hold_of_a_confirmed_reservation_changes_nothing()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();
        var reservation = await client.ReserveOkAsync(edition.Id);
        await client.ConfirmAsync(reservation.Id);

        var held = await client.HoldOkAsync(reservation.Id);

        Assert.Equal("Confirmed", held.Status);
        Assert.Equal(reservation.ExpiresAt, held.ExpiresAt);
    }

    private async Task SweepAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReservationService>().SweepAsync(fixture.Clock.GetUtcNowToMicrosecond(), Ct);
    }
}
