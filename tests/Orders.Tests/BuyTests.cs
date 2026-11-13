using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.Orders.Orders;

namespace NewStore.Orders.Tests;

[Collection("orders")]
public class BuyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OrdersFixture _fixture;

    public BuyTests(OrdersFixture fixture)
    {
        _fixture = fixture;
        fixture.Reset();
    }

    private async Task<HttpResponseMessage> BuyAsync(Guid buyer, Guid editionId)
    {
        using var client = _fixture.BuyerClient(buyer);
        return await client.PostAsJsonAsync("/orders", new { editionId }, Ct);
    }

    private Task<int> CountOrdersAsync(Guid buyer) =>
        _fixture.QueryAsync(db => db.Orders.CountAsync(o => o.BuyerId == buyer, Ct));

    [Fact]
    public async Task Buy_reserves_and_returns_the_order()
    {
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();

        var response = await BuyAsync(buyer, edition);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!;
        Assert.Equal(edition, order.EditionId);
        Assert.Equal("Reserved", order.Status);
        Assert.Equal(9.99m, order.Price);
        Assert.Equal(InventoryStub.VideoId, order.VideoId);
        Assert.Equal("Hd", order.Format);

        // The response shows what is stored: the row has the same deadline, and it came from Inventory unchanged.
        var stored = await _fixture.GetOrderAsync(order.Id);
        Assert.Equal(stored.ReservedUntil, order.ReservedUntil);
        Assert.Equal(buyer, stored.BuyerId);
        Assert.Equal(OrderStatus.Reserved, stored.Status);
        var reserve = Assert.Single(_fixture.Inventory.ReservesFor(edition));
        Assert.Equal(stored.ReservationId, StubReplies.Body(reserve).GetProperty("reservationId").GetGuid());
        Assert.Equal(buyer, StubReplies.Body(reserve).GetProperty("buyerId").GetGuid());
    }

    [Fact]
    public async Task Buy_when_sold_out_returns_409_and_keeps_no_order()
    {
        var buyer = Guid.NewGuid();
        _fixture.Inventory.Reserve = _ => StubReplies.Problem(HttpStatusCode.Conflict, "sold-out", "Every copy of this edition is taken.");

        var response = await BuyAsync(buyer, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("urn:newstore:problem:sold-out", problem.GetProperty("type").GetString());
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Buying_an_edition_you_own_returns_409()
    {
        var buyer = Guid.NewGuid();
        _fixture.Inventory.Reserve = _ => StubReplies.Problem(HttpStatusCode.Conflict, "already-holding");

        var response = await BuyAsync(buyer, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:already-holding",
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString());
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Buying_a_missing_edition_returns_404()
    {
        _fixture.Inventory.Reserve = _ => StubReplies.Problem(HttpStatusCode.NotFound, "not-found");

        var response = await BuyAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Buy_retries_with_the_same_reservation_id_after_a_lost_reply()
    {
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var original = _fixture.Inventory.Reserve;
        var calls = 0;
        // The first attempt gets a 503 (here Inventory did nothing; a lost 201 is the same to the caller), the retry gets the normal 201.
        _fixture.Inventory.Reserve = r => Interlocked.Increment(ref calls) == 1 ? StubReplies.Unavailable() : original(r);

        var response = await BuyAsync(buyer, edition);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reserves = _fixture.Inventory.ReservesFor(edition);
        Assert.Equal(2, reserves.Count);
        Assert.Single(reserves.Select(r => StubReplies.Body(r).GetProperty("reservationId").GetGuid()).Distinct());
        Assert.Equal(1, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Buy_after_a_lost_reply_accepts_200_pending()
    {
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var original = _fixture.Inventory.Reserve;
        // Inventory committed the first request but its 201 was lost; the retry with the same id is answered 200 Pending.
        _fixture.Inventory.Reserve = r =>
        {
            original(r);
            return original(r);
        };

        var response = await BuyAsync(buyer, edition);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountOrdersAsync(buyer));
    }

    [Theory]
    [InlineData("Expired", "reservation-expired")]
    [InlineData("Released", "reservation-released")]
    public async Task A_200_for_an_ended_reservation_is_a_409_and_keeps_no_order(string status, string slug)
    {
        var buyer = Guid.NewGuid();
        var original = _fixture.Inventory.Reserve;
        _fixture.Inventory.Reserve = r =>
        {
            var reply = original(r);
            var body = reply.Content.ReadFromJsonAsync<JsonElement>(Ct).GetAwaiter().GetResult();
            return StubReplies.Json(HttpStatusCode.OK, new
            {
                id = body.GetProperty("id").GetGuid(),
                videoId = InventoryStub.VideoId,
                format = "Hd",
                price = 9.99m,
                status,
                expiresAt = DateTimeOffset.UtcNow,
            });
        };

        var response = await BuyAsync(buyer, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"urn:newstore:problem:{slug}",
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString());
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Buy_again_while_an_order_is_open_returns_that_order()
    {
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var first = await _fixture.BuyAsync(buyer, edition);

        var response = await BuyAsync(buyer, edition);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first.Id, (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!.Id);
        Assert.Single(_fixture.Inventory.ReservesFor(edition));
        Assert.Equal(1, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task A_double_click_on_buy_gets_the_same_order_twice()
    {
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var original = _fixture.Inventory.Reserve;
        var arrived = 0;
        var second = new TaskCompletionSource();
        // Inventory's unique (edition, buyer) index: the first reserve wins, the second is answered already-holding. The first waits
        // for the second to arrive, so both passed the open-order check with no order yet; the second's answer waits until the
        // first request has saved its order.
        _fixture.Inventory.Reserve = r =>
        {
            if (Interlocked.Increment(ref arrived) == 1)
            {
                Assert.True(second.Task.Wait(TimeSpan.FromSeconds(10)), "The second buy never reached Inventory.");
                return original(r);
            }

            second.SetResult();
            OrdersFixture.WaitAsync(async () => await CountOrdersAsync(buyer) == 1, "the first order is saved").GetAwaiter().GetResult();
            return StubReplies.Problem(HttpStatusCode.Conflict, "already-holding");
        };

        var replies = await Task.WhenAll(BuyAsync(buyer, edition), BuyAsync(buyer, edition));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], replies.Select(r => r.StatusCode).Order());
        var ids = await Task.WhenAll(replies.Select(async r => (await r.Content.ReadFromJsonAsync<OrderResponse>(Ct))!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Buy_again_after_the_reservation_ran_out_reserves_again()
    {
        var buyer = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var first = await _fixture.BuyAsync(buyer, edition);
        await _fixture.QueryAsync(async db =>
            await db.Orders.Where(o => o.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.ReservedUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        var response = await BuyAsync(buyer, edition);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(first.Id, (await response.Content.ReadFromJsonAsync<OrderResponse>(Ct))!.Id);
        Assert.Equal(2, _fixture.Inventory.ReservesFor(edition).Count);
    }

    [Fact]
    public async Task Buy_needs_a_signed_in_buyer()
    {
        var before = _fixture.Inventory.Handler.Requests.Count;
        using var anonymous = _fixture.CreateClient();
        var noToken = await anonymous.PostAsJsonAsync("/orders", new { editionId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

        using var service = _fixture.CreateClient().WithBearer(TestTokens.Client("inventory", "orders"));
        var serviceToken = await service.PostAsJsonAsync("/orders", new { editionId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, serviceToken.StatusCode);

        // Neither request got as far as Inventory.
        Assert.Equal(before, _fixture.Inventory.Handler.Requests.Count);
    }

    [Fact]
    public async Task Buy_without_an_edition_is_400()
    {
        var response = await BuyAsync(Guid.NewGuid(), Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Inventory_answering_429_or_408_on_buy_is_503_with_retry_after(HttpStatusCode status)
    {
        var buyer = Guid.NewGuid();
        _fixture.Inventory.Reserve = _ => new HttpResponseMessage(status);

        var response = await BuyAsync(buyer, Guid.NewGuid());

        // After the pipeline's retries the buyer is told to try again, not shown a 500.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(5, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }

    [Fact]
    public async Task Inventory_down_on_buy_is_503_with_retry_after()
    {
        var buyer = Guid.NewGuid();
        _fixture.Inventory.Reserve = _ => StubReplies.Unavailable();

        var response = await BuyAsync(buyer, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(5, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Equal("urn:newstore:problem:dependency-unavailable",
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString());
        Assert.Equal(0, await CountOrdersAsync(buyer));
    }
}
