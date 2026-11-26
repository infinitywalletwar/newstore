using Microsoft.EntityFrameworkCore;

namespace NewStore.Delivery.Tests;

public static class CourierTestData
{
    /// <summary>
    /// Marks every open shipment finished, so a test's claims see only the shipments it creates. A claim takes the oldest open
    /// shipment, and the tests of one class share a database.
    /// </summary>
    public static Task CloseOpenShipmentsAsync(this DeliveryFixture fixture)
    {
        var now = fixture.Clock.GetUtcNow();
        return fixture.QueryAsync(db => db.Shipments
            .Where(s => s.CourierFinishedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(s => s.CourierFinishedAt, now)
                    .SetProperty(s => s.CourierLockedBy, (string?)null)
                    .SetProperty(s => s.CourierLockedUntil, (DateTimeOffset?)null),
                TestContext.Current.CancellationToken));
    }
}
