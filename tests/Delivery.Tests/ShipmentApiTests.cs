using System.Text.Json.Nodes;

using NewStore.Delivery.Shipments;

namespace NewStore.Delivery.Tests;

public sealed class ShipmentApiTests(DeliveryFixture delivery) : IClassFixture<DeliveryFixture>
{
    /// <summary>The seqs in the order the path test inserts them.</summary>
    private static readonly int[] s_outOfOrder = [2, 0, 1];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task My_shipments_lists_the_callers_newest_first()
    {
        var buyer = Guid.NewGuid();
        var older = await delivery.CreateShipmentAsync(buyer);
        delivery.Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await delivery.CreateShipmentAsync(buyer);
        await delivery.CreateShipmentAsync(); // another buyer's
        using var client = delivery.BuyerClient(buyer);

        var shipments = await (await client.GetAsync("/me/shipments", Ct)).ReadAsync<List<ShipmentSummary>>();

        Assert.Equal([newer.Id, older.Id], shipments.Select(s => s.Id));
        var first = shipments[0];
        Assert.Equal(new ShipmentSummary(newer.Id, newer.OrderId, newer.CopyNumber, newer.VideoId, "Preparing", newer.CreatedAt, null), first);
    }

    [Fact]
    public async Task Shipment_details_have_the_destination_the_warehouse_and_an_empty_path()
    {
        var buyer = Guid.NewGuid();
        var shipment = await delivery.CreateShipmentAsync(buyer, latitude: 52.5163, longitude: 13.3777);
        using var client = delivery.BuyerClient(buyer);

        var details = await (await client.GetAsync($"/shipments/{shipment.Id}", Ct)).ReadAsync<ShipmentDetails>();

        Assert.Equal(shipment.Id, details.Id);
        Assert.Equal(shipment.OrderId, details.OrderId);
        Assert.Equal(shipment.CopyNumber, details.CopyNumber);
        Assert.Equal(shipment.VideoId, details.VideoId);
        Assert.Equal("Preparing", details.Status);
        Assert.Equal(shipment.Address, details.Address);
        Assert.Equal(new GeoPoint(52.5163, 13.3777), details.Destination);
        Assert.Equal(new WarehouseResponse("newstore warehouse, Tempelhof, Berlin", 52.4836, 13.3897), details.Warehouse);
        Assert.Null(details.LastPosition);
        Assert.Empty(details.Path);
        Assert.Equal(shipment.CreatedAt, details.CreatedAt);
        Assert.Null(details.PickedUpAt);
        Assert.Null(details.DeliveredAt);
    }

    [Fact]
    public async Task Shipment_details_list_the_path_oldest_first()
    {
        var buyer = Guid.NewGuid();
        var shipment = await delivery.CreateShipmentAsync(buyer);
        var start = Api.Micro(delivery.Clock.GetUtcNow());
        // Inserted out of order: the answer orders by seq, not by insertion.
        await delivery.QueryAsync(async db =>
        {
            foreach (var seq in s_outOfOrder)
            {
                db.ShipmentPositions.Add(new ShipmentPosition
                {
                    ShipmentId = shipment.Id,
                    Seq = seq,
                    Latitude = 52.4836 + (seq * 0.001),
                    Longitude = 13.3897,
                    Status = seq == 0 ? ShipmentStatus.Preparing : ShipmentStatus.InTransit,
                    RecordedAt = start.AddSeconds(seq * 2),
                });
            }

            return await db.SaveChangesAsync(Ct);
        });
        using var client = delivery.BuyerClient(buyer);

        var details = await (await client.GetAsync($"/shipments/{shipment.Id}", Ct)).ReadAsync<ShipmentDetails>();

        Assert.Equal([0, 1, 2], details.Path.Select(p => p.Seq));
        Assert.Equal(new PositionResponse(0, "Preparing", 52.4836, 13.3897, start), details.Path[0]);
        Assert.Equal(details.Path[^1], details.LastPosition);
        Assert.Equal(2, details.LastPosition!.Seq);
        Assert.Equal("InTransit", details.LastPosition.Status);
    }

    [Fact]
    public async Task Someone_elses_shipment_is_404_like_a_missing_one()
    {
        // A shipment holds the buyer's address: a stranger must not even learn that the id exists.
        var shipment = await delivery.CreateShipmentAsync();
        using var stranger = delivery.BuyerClient(Guid.NewGuid());

        var someoneElses = await stranger.GetAsync($"/shipments/{shipment.Id}", Ct);
        var missing = await stranger.GetAsync($"/shipments/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("urn:newstore:problem:not-found", (await someoneElses.ProblemAsync()).Type);
        Assert.Equal(await BodyWithoutTraceIdAsync(missing), await BodyWithoutTraceIdAsync(someoneElses));
    }

    [Theory]
    [InlineData("/me/shipments")]
    [InlineData("/shipments/0198a7a4-0000-7000-8000-000000000001")]
    public async Task Shipment_endpoints_need_a_signed_in_user(string path)
    {
        using var anonymous = delivery.CreateClient();
        using var service = delivery.CreateClient().WithBearer(TestTokens.Client("orders", "delivery"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync(path, Ct)).StatusCode);
    }

    /// <summary>The problem's JSON without <c>traceId</c>, which names the request and so differs between any two answers.</summary>
    private static async Task<string> BodyWithoutTraceIdAsync(HttpResponseMessage response)
    {
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        body.Remove("traceId");
        return body.ToJsonString();
    }
}
