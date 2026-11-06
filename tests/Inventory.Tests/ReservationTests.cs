using System.Net.Http.Json;

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
}

public sealed class ReservationTests_ConditionalUpdate(ConditionalUpdateFixture fixture)
    : ReservationTests(fixture), IClassFixture<ConditionalUpdateFixture>;

public sealed class ReservationTests_ConcurrencyToken(ConcurrencyTokenFixture fixture)
    : ReservationTests(fixture), IClassFixture<ConcurrencyTokenFixture>;
