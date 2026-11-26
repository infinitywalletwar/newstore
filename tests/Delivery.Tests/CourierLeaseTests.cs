using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Delivery.Courier;
using NewStore.Delivery.Shipments;

namespace NewStore.Delivery.Tests;

/// <summary>
/// The lease on <c>shipments</c>, on role <c>Api</c> (no courier runs) and the fixture's fake clock, with the default 30 s lease.
/// A claim takes the oldest open shipment, so each test first closes what earlier tests left open.
/// </summary>
public sealed class CourierLeaseTests(DeliveryFixture delivery) : IClassFixture<DeliveryFixture>
{
    private static readonly TimeSpan Lease = new CourierOptions().Lease;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> WithLeasesAsync<T>(Func<CourierLeases, Task<T>> action)
    {
        await using var scope = delivery.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<CourierLeases>());
    }

    private Task<ClaimedShipment?> ClaimAsync(string courierId) => WithLeasesAsync(l => l.ClaimAsync(courierId, Ct));

    private Task<Shipment> RowAsync(Guid id) =>
        delivery.QueryAsync(db => db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id, Ct));

    private DateTimeOffset Now => Api.Micro(delivery.Clock.GetUtcNow());

    [Fact]
    public async Task Claim_sets_started_at_once_and_keeps_it_on_a_takeover()
    {
        await delivery.CloseOpenShipmentsAsync();
        var shipment = await delivery.CreateShipmentAsync();

        var first = await ClaimAsync("a");
        Assert.NotNull(first);
        Assert.Equal(shipment.Id, first.Id);
        Assert.Equal(Now, first.StartedAt);
        Assert.Equal(new GeoPoint(shipment.WarehouseLatitude, shipment.WarehouseLongitude), first.From);
        Assert.Equal(new GeoPoint(shipment.Latitude, shipment.Longitude), first.To);

        // a stops renewing; once its lease has run out, b takes the trip over.
        delivery.Clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var second = await ClaimAsync("b");

        Assert.NotNull(second);
        Assert.Equal(shipment.Id, second.Id);
        Assert.Equal(first.StartedAt, second.StartedAt);
        var row = await RowAsync(shipment.Id);
        Assert.Equal("b", row.CourierLockedBy);
        Assert.Equal(Now + Lease, row.CourierLockedUntil);
        Assert.Equal(first.StartedAt, row.CourierStartedAt);
    }

    [Fact]
    public async Task Two_couriers_never_claim_the_same_shipment()
    {
        await delivery.CloseOpenShipmentsAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            created.Add((await delivery.CreateShipmentAsync()).Id);
        }

        // Four claimers at once, each with its own scope (so its own connection), each claiming until nothing is left.
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(
            () => WithLeasesAsync(async leases =>
            {
                var mine = new List<Guid>();
                while (await leases.ClaimAsync($"courier-{i}", Ct) is { } claim)
                {
                    mine.Add(claim.Id);
                }

                return mine;
            }),
            Ct)));

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(20, all.Count);
        Assert.Equal(created.Order(), all.Order());
    }

    [Fact]
    public async Task Finished_shipment_is_never_claimed_again()
    {
        await delivery.CloseOpenShipmentsAsync();
        var shipment = await delivery.CreateShipmentAsync();
        Assert.NotNull(await ClaimAsync("a"));

        Assert.True(await WithLeasesAsync(l => l.FinishAsync(shipment.Id, "a", Ct)));

        var row = await RowAsync(shipment.Id);
        Assert.Equal(Now, row.CourierFinishedAt);
        Assert.Null(row.CourierLockedBy);
        Assert.Null(row.CourierLockedUntil);

        delivery.Clock.Advance(Lease + TimeSpan.FromSeconds(1));
        Assert.Null(await ClaimAsync("b"));
        Assert.False(await WithLeasesAsync(l => l.FinishAsync(shipment.Id, "b", Ct)));
    }

    [Fact]
    public async Task Renew_returns_only_the_shipments_still_held()
    {
        await delivery.CloseOpenShipmentsAsync();
        var older = await delivery.CreateShipmentAsync();
        delivery.Clock.Advance(TimeSpan.FromMilliseconds(1));   // so the claim's ORDER BY created_at picks older first
        var newer = await delivery.CreateShipmentAsync();
        Assert.Equal(older.Id, (await ClaimAsync("a"))?.Id);
        Assert.Equal(newer.Id, (await ClaimAsync("a"))?.Id);

        // Both of a's leases run out; b takes the older one. The newer one is still a's: nobody else has claimed it.
        delivery.Clock.Advance(Lease + TimeSpan.FromSeconds(1));
        Assert.Equal(older.Id, (await ClaimAsync("b"))?.Id);

        var held = await WithLeasesAsync(l => l.RenewAsync("a", [older.Id, newer.Id], Ct));

        Assert.Equal([newer.Id], held);
        Assert.Equal(Now + Lease, (await RowAsync(newer.Id)).CourierLockedUntil);
        Assert.Equal("b", (await RowAsync(older.Id)).CourierLockedBy);
    }

    [Fact]
    public async Task Release_lets_another_courier_claim_at_once()
    {
        await delivery.CloseOpenShipmentsAsync();
        var shipment = await delivery.CreateShipmentAsync();
        Assert.NotNull(await ClaimAsync("a"));
        Assert.Null(await ClaimAsync("b"));

        await WithLeasesAsync(async l =>
        {
            await l.ReleaseAsync("a", Ct);
            return true;
        });
        Assert.Equal(Now, (await RowAsync(shipment.Id)).CourierLockedUntil);

        // b's next tick, long before a's 30 s lease would have run out.
        delivery.Clock.Advance(new CourierOptions().TickInterval);
        var claim = await ClaimAsync("b");

        Assert.Equal(shipment.Id, claim?.Id);
        Assert.Equal("b", (await RowAsync(shipment.Id)).CourierLockedBy);
    }
}
