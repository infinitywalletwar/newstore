using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.Orders.Orders;

namespace NewStore.Orders.Tests;

[Collection("orders")]
public class PayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly OrdersFixture _fixture;

    public PayTests(OrdersFixture fixture)
    {
        _fixture = fixture;
        fixture.Reset();
    }

    private async Task<HttpResponseMessage> PayAsync(Guid buyer, Guid orderId)
    {
        using var client = _fixture.BuyerClient(buyer);
        return await client.PostAsync($"/orders/{orderId}/pay", null, Ct);
    }

    private static async Task<string> TypeOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString()!;

    [Fact]
    public async Task Pay_holds_then_creates_the_payment_and_returns_its_url()
    {
        var buyer = Guid.NewGuid();
        var order = await _fixture.BuyAsync(buyer);

        var response = await PayAsync(buyer, order.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<PayResponse>(Ct))!.CheckoutUrl;
        var stored = await _fixture.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Paying, stored.Status);
        Assert.Equal(url, stored.CheckoutUrl);
        Assert.NotNull(stored.PaymentId);
        Assert.EndsWith(stored.PaymentId.ToString()!, url, StringComparison.Ordinal);
        // The hold's deadline replaced the reservation's.
        Assert.True(stored.ReservedUntil > order.ReservedUntil);

        // The hold came first: Orders asks Inventory, and only then creates the payment.
        var log = OrdersFixture.Calls.Entries;
        Assert.True(log.IndexOf($"hold {stored.ReservationId}") < log.IndexOf($"create {order.Id}"), string.Join(", ", log.TakeLast(4)));
        Assert.Contains($"hold {stored.ReservationId}", log);
        // The payment carries the order's price and the way back to the order page.
        var hold = Assert.Single(_fixture.Inventory.RequestsFor(stored.ReservationId));
        Assert.EndsWith("/hold", hold.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var create = StubReplies.Body(Assert.Single(_fixture.Payments.CreatesFor(order.Id)));
        Assert.Equal(9.99m, create.GetProperty("amount").GetDecimal());
        Assert.Equal($"http://shop.test/orders/{order.Id}", create.GetProperty("returnUrl").GetString());
    }

    [Fact]
    public async Task Pay_on_a_paying_order_returns_the_stored_url_and_calls_nobody()
    {
        var paying = await _fixture.PayingOrderAsync();
        var calls = _fixture.Payments.CreatesFor(paying.Id).Count;

        var response = await PayAsync(paying.BuyerId, paying.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(paying.CheckoutUrl, (await response.Content.ReadFromJsonAsync<PayResponse>(Ct))!.CheckoutUrl);
        Assert.Equal(calls, _fixture.Payments.CreatesFor(paying.Id).Count);
    }

    [Fact]
    public async Task Double_pay_click_creates_one_payment()
    {
        var buyer = Guid.NewGuid();
        var order = await _fixture.BuyAsync(buyer);
        var original = _fixture.Payments.Create;
        // Make the overlap certain: the first create waits until the second one has arrived, so both pays are between hold and
        // record at the same moment, and exactly one of the two saves can win the order's row.
        var arrived = 0;
        var both = new TaskCompletionSource();
        _fixture.Payments.Create = r =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.SetResult();
            }

            Assert.True(both.Task.Wait(TimeSpan.FromSeconds(10)), "The second pay never reached Payments.");
            return original(r);
        };
        var before = _fixture.Payments.Handler.Requests.Count;

        var replies = await Task.WhenAll(PayAsync(buyer, order.Id), PayAsync(buyer, order.Id));

        // Both answer 200 with the same page, whichever request lost the race for the order's row.
        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var urls = await Task.WhenAll(replies.Select(async r => (await r.Content.ReadFromJsonAsync<PayResponse>(Ct))!.CheckoutUrl));
        Assert.Single(urls.Distinct());
        // Every request Payments received since the click names this order: Payments' unique order id makes that one payment, and the
        // stub made exactly one, which is the one the order recorded.
        var created = _fixture.Payments.Handler.Requests.Skip(before).ToList();
        Assert.Equal(2, created.Count);
        Assert.All(created, r => Assert.Equal(order.Id, StubReplies.Body(r).GetProperty("orderId").GetGuid()));
        var stored = await _fixture.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Paying, stored.Status);
        Assert.Equal(_fixture.Payments.PaymentIdFor(order.Id), stored.PaymentId);
        Assert.Equal(urls[0], stored.CheckoutUrl);
    }

    [Fact]
    public async Task Payments_down_on_pay_keeps_the_holds_deadline()
    {
        var buyer = Guid.NewGuid();
        var order = await _fixture.BuyAsync(buyer);
        _fixture.Payments.Create = _ => StubReplies.Unavailable();

        var response = await PayAsync(buyer, order.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The hold's deadline (45 minutes) replaced the reservation's (10) even though the payment failed: a buyer who comes back
        // after minute 10 still finds the order open.
        var stored = await _fixture.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Reserved, stored.Status);
        Assert.True(stored.ReservedUntil > order.ReservedUntil.AddMinutes(30), $"ReservedUntil is {stored.ReservedUntil}.");
        using var client = _fixture.BuyerClient(buyer);
        var again = await client.PostAsJsonAsync("/orders", new { editionId = order.EditionId }, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(order.Id, (await again.Content.ReadFromJsonAsync<OrderResponse>(Ct))!.Id);
    }

    [Fact]
    public async Task Pay_that_loses_the_race_to_a_payment_event_answers_from_the_stored_state()
    {
        var buyer = Guid.NewGuid();
        var order = await _fixture.BuyAsync(buyer);
        var original = _fixture.Payments.Create;
        // While Orders waits for Payments, the saga completes the order: the row changes under the pay request.
        _fixture.Payments.Create = r =>
        {
            _fixture.QueryAsync(db => db.Orders.Where(o => o.Id == order.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Completed), Ct)).GetAwaiter().GetResult();
            return original(r);
        };

        var response = await PayAsync(buyer, order.Id);

        // The pay request's save failed on xmin. It reloaded, saw Completed and answered from that, instead of writing Paying over it.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:order-closed", await TypeOfAsync(response));
        Assert.Equal(OrderStatus.Completed, (await _fixture.GetOrderAsync(order.Id)).Status);
    }

    [Fact]
    public async Task Pay_after_the_reservation_ran_out_marks_the_order_expired()
    {
        var buyer = Guid.NewGuid();
        var order = await _fixture.BuyAsync(buyer);
        _fixture.Inventory.Hold = _ => StubReplies.Problem(HttpStatusCode.Conflict, "reservation-expired");

        var response = await PayAsync(buyer, order.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:order-expired", await TypeOfAsync(response));
        Assert.Equal(OrderStatus.Expired, (await _fixture.GetOrderAsync(order.Id)).Status);
        Assert.Empty(_fixture.Payments.CreatesFor(order.Id));

        // Paying again gets the same answer without a call to Inventory.
        var holds = _fixture.Inventory.RequestsFor((await _fixture.GetOrderAsync(order.Id)).ReservationId).Count;
        var again = await PayAsync(buyer, order.Id);
        Assert.Equal("urn:newstore:problem:order-expired", await TypeOfAsync(again));
        Assert.Equal(holds, _fixture.Inventory.RequestsFor((await _fixture.GetOrderAsync(order.Id)).ReservationId).Count);
    }

    [Fact]
    public async Task Pay_on_a_closed_order_is_409_order_closed()
    {
        var order = await _fixture.PayingOrderAsync();
        await _fixture.QueryAsync(async db =>
            await db.Orders.Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Failed), Ct));

        var response = await PayAsync(order.BuyerId, order.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("urn:newstore:problem:order-closed", await TypeOfAsync(response));
    }

    [Fact]
    public async Task Payments_down_on_pay_is_503_and_the_order_stays_reserved()
    {
        var buyer = Guid.NewGuid();
        var order = await _fixture.BuyAsync(buyer);
        _fixture.Payments.Create = _ => StubReplies.Unavailable();

        var response = await PayAsync(buyer, order.Id);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(5, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Equal(OrderStatus.Reserved, (await _fixture.GetOrderAsync(order.Id)).Status);

        // Payments is back: the second click repeats the hold and the create, and the order goes on.
        _fixture.Reset();
        var retry = await PayAsync(buyer, order.Id);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(OrderStatus.Paying, (await _fixture.GetOrderAsync(order.Id)).Status);
    }

    [Fact]
    public async Task Another_buyers_order_is_404()
    {
        var order = await _fixture.BuyAsync(Guid.NewGuid());
        var stranger = Guid.NewGuid();

        using var client = _fixture.BuyerClient(stranger);
        var get = await client.GetAsync($"/orders/{order.Id}", Ct);
        var pay = await PayAsync(stranger, order.Id);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, pay.StatusCode);
        Assert.Equal(OrderStatus.Reserved, (await _fixture.GetOrderAsync(order.Id)).Status);
    }

    [Fact]
    public async Task The_order_and_the_copy_list_use_the_names_the_web_app_reads()
    {
        var paying = await _fixture.PayingOrderAsync();
        using var client = _fixture.BuyerClient(paying.BuyerId);

        var order = await client.GetFromJsonAsync<JsonElement>($"/orders/{paying.Id}", Ct);

        Assert.Equal(
            ["checkoutUrl", "copyNumber", "editionId", "format", "id", "price", "reservedUntil", "status", "videoId"],
            order.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.String, order.GetProperty("status").ValueKind);
        Assert.Equal("Paying", order.GetProperty("status").GetString());
    }
}
