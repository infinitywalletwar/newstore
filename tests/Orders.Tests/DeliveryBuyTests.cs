using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.Orders.Orders;

namespace NewStore.Orders.Tests;

[Collection("orders")]
public class DeliveryBuyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OrdersFixture _fixture;

    public DeliveryBuyTests(OrdersFixture fixture)
    {
        _fixture = fixture;
        fixture.Reset();
    }

    private async Task<HttpResponseMessage> BuyAsync(Guid buyer, Guid edition, object? delivery)
    {
        using var client = _fixture.BuyerClient(buyer);
        return await client.PostAsJsonAsync("/orders", new { editionId = edition, delivery }, Ct);
    }

    private static object Delivery(string address = "  Alexanderplatz 1, 10178 Berlin ", double latitude = 52.5219, double longitude = 13.4132) =>
        new { address, latitude, longitude };

    private Task<int> CountOrdersAsync(Guid buyer) =>
        _fixture.QueryAsync(db => db.Orders.CountAsync(o => o.BuyerId == buyer, Ct));

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>The release requests Orders sent for the reservations it made for this edition.</summary>
    private List<HttpRequestMessage> ReleasesFor(Guid edition) =>
        [.. _fixture.Inventory.ReservesFor(edition)
            .SelectMany(r => _fixture.Inventory.RequestsFor(StubReplies.Body(r).GetProperty("reservationId").GetGuid()))
            .Where(r => r.RequestUri!.AbsolutePath.EndsWith("/release", StringComparison.Ordinal))];

    [Fact]
    public async Task Dvd_buy_with_delivery_stores_the_address()
    {
        _fixture.Inventory.Format = "Dvd";
        var buyer = Guid.NewGuid();

        var response = await BuyAsync(buyer, Guid.NewGuid(), Delivery());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
        var stored = await _fixture.GetOrderAsync(order.Id);
        Assert.Equal("Dvd", stored.Format);
        Assert.Equal("Alexanderplatz 1, 10178 Berlin", stored.DeliveryAddress);
        Assert.Equal(52.5219, stored.DeliveryLatitude);
        Assert.Equal(13.4132, stored.DeliveryLongitude);
    }

    [Fact]
    public async Task Dvd_buy_without_delivery_is_400_and_releases_the_reservation()
    {
        _fixture.Inventory.Format = "Dvd";
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();

        var response = await BuyAsync(buyer, edition, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("urn:newstore:problem:delivery-required", problem.GetProperty("type").GetString());
        Assert.Single(ReleasesFor(edition));
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Digital_buy_with_delivery_is_400_and_releases_the_reservation()
    {
        _fixture.Inventory.Format = "Digital";
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();

        var response = await BuyAsync(buyer, edition, Delivery());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("urn:newstore:problem:delivery-not-allowed", problem.GetProperty("type").GetString());
        Assert.Single(ReleasesFor(edition));
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Theory]
    [InlineData("  ", 52.0, 13.0, "delivery.address")]
    [InlineData("201", 52.0, 13.0, "delivery.address")]
    [InlineData("ok", 90.0001, 13.0, "delivery.latitude")]
    [InlineData("ok", -91.0, 13.0, "delivery.latitude")]
    [InlineData("ok", 52.0, 180.5, "delivery.longitude")]
    [InlineData("ok", null, 13.0, "delivery.latitude")]
    public async Task Invalid_delivery_is_400_without_reserving(string address, double? latitude, double longitude, string key)
    {
        _fixture.Inventory.Format = "Dvd";
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var text = address == "201" ? new string('a', 201) : address;

        var response = await BuyAsync(buyer, edition, new { address = text, latitude, longitude });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("urn:newstore:problem:validation", problem.GetProperty("type").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(key, out _), $"errors has no {key}");
        Assert.Empty(_fixture.Inventory.ReservesFor(edition));
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Boundary_coordinates_are_accepted()
    {
        _fixture.Inventory.Format = "Dvd";

        var response = await BuyAsync(Guid.NewGuid(), Guid.NewGuid(), Delivery(new string('a', 200), -90, 180));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Release_failure_still_answers_400()
    {
        _fixture.Inventory.Format = "Dvd";
        _fixture.Inventory.Release = _ => StubReplies.Unavailable();
        var buyer = Guid.NewGuid();

        var response = await BuyAsync(buyer, Guid.NewGuid(), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("urn:newstore:problem:delivery-required", problem.GetProperty("type").GetString());
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Digital_buy_without_delivery_is_unchanged()
    {
        _fixture.Inventory.Format = "Digital";
        var buyer = Guid.NewGuid();

        var response = await BuyAsync(buyer, Guid.NewGuid(), null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
        var stored = await _fixture.GetOrderAsync(order.Id);
        Assert.Null(stored.DeliveryAddress);
        Assert.Null(stored.DeliveryLatitude);
        Assert.Null(stored.DeliveryLongitude);
    }
}
