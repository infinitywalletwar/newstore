using Microsoft.EntityFrameworkCore;

using NewStore.Delivery.Shipments;

namespace NewStore.Delivery.Tests;

public sealed class DeliveryStartupTests(DeliveryFixture delivery) : IClassFixture<DeliveryFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delivery_is_ready_with_its_tables()
    {
        using var client = delivery.CreateClient();

        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(Ct));
        var tables = await delivery.QueryAsync(db => db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync(Ct));
        foreach (var table in new[] { "shipments", "shipment_positions", "outbox_messages", "inbox_messages" })
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task Warehouse_is_anonymous_and_comes_from_configuration()
    {
        using var client = delivery.CreateClient();

        var warehouse = await (await client.GetAsync("/delivery/warehouse", Ct)).ReadAsync<WarehouseResponse>();

        Assert.Equal(new WarehouseResponse("newstore warehouse, Tempelhof, Berlin", 52.4836, 13.3897), warehouse);
    }
}
