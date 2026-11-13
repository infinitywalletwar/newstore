using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using NewStore.Payments.Payments;

namespace NewStore.Payments.Tests;

[Collection("payments")]
public class PaymentApiTests(PaymentsFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_payment_returns_the_checkout_url()
    {
        var orderId = Guid.NewGuid();

        var payment = await fixture.CreatePaymentAsync(orderId, amount: 12.50m);

        Assert.Equal(orderId, payment.OrderId);
        Assert.Equal(12.50m, payment.Amount);
        Assert.Equal("Created", payment.Status);
        Assert.Equal($"http://fakepay.test/checkout/cs_{payment.Id}", payment.CheckoutUrl);
        Assert.NotNull(payment.SessionExpiresAt);

        // The provider got the attempt's id as the idempotency key and the bearer key from configuration.
        var request = Assert.Single(fixture.Provider.SessionRequestsFor(orderId));
        Assert.Equal(payment.Id.ToString(), request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());

        using var client = fixture.CreateOrdersClient();
        var fetched = await client.GetFromJsonAsync<PaymentResponse>($"/payments/{payment.Id}", Ct);
        Assert.Equal(payment, fetched);
    }

    [Fact]
    public async Task Second_create_for_the_same_order_returns_the_first_payment()
    {
        var orderId = Guid.NewGuid();
        var first = await fixture.CreatePaymentAsync(orderId);

        using var client = fixture.CreateOrdersClient();
        var again = await client.PostAsJsonAsync("/payments", new { orderId, amount = 9.99m, returnUrl = "http://shop.test/orders" }, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(first, await again.Content.ReadFromJsonAsync<PaymentResponse>(Ct));
        Assert.Single(fixture.Provider.SessionRequestsFor(orderId));
    }

    [Fact]
    public async Task Parallel_creates_for_one_order_make_one_payment()
    {
        var orderId = Guid.NewGuid();
        using var client = fixture.CreateOrdersClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            client.PostAsJsonAsync("/payments", new { orderId, amount = 9.99m, returnUrl = "http://shop.test/orders" }, Ct)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{r.StatusCode}"));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var payments = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<PaymentResponse>(Ct)));
        Assert.Single(payments.Select(p => p!.Id).Distinct());
        Assert.All(payments, p => Assert.NotNull(p!.CheckoutUrl));
        Assert.Equal(1, await fixture.QueryAsync(db => db.PaymentAttempts.CountAsync(a => a.OrderId == orderId, Ct)));
        Assert.Single(fixture.Provider.SessionRequestsFor(orderId));
    }

    [Fact]
    public async Task Only_the_orders_client_may_create_a_payment()
    {
        var body = new { orderId = Guid.NewGuid(), amount = 9.99m, returnUrl = "http://shop.test/orders" };

        using var anonymous = fixture.CreateClient();
        using var inventory = fixture.CreateClient().WithBearer(TestTokens.Client("inventory", "payments"));
        using var user = fixture.CreateClient().WithBearer(TestTokens.User(Guid.NewGuid(), "payments"));
        using var wrongAudience = fixture.CreateClient().WithBearer(TestTokens.Client("orders", "inventory"));
        using var orders = fixture.CreateOrdersClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/payments", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inventory.PostAsJsonAsync("/payments", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/payments", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongAudience.PostAsJsonAsync("/payments", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await orders.PostAsJsonAsync("/payments", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/payments/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Provider_down_gives_503_and_a_retry_completes_the_same_payment()
    {
        var orderId = Guid.NewGuid();
        var original = fixture.Provider.CreateSession;
        fixture.Provider.CreateSession = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        try
        {
            using var client = fixture.CreateOrdersClient();
            var down = await client.PostAsJsonAsync("/payments", new { orderId, amount = 9.99m, returnUrl = "http://shop.test/orders" }, Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            Assert.True(down.Headers.RetryAfter is not null);
            Assert.Contains("dependency-unavailable", await down.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            fixture.Provider.CreateSession = original;
        }

        // The attempt was kept, without a session. The retry finishes it with the same idempotency key.
        var kept = await fixture.QueryAsync(db => db.PaymentAttempts.SingleAsync(a => a.OrderId == orderId, Ct));
        Assert.Null(kept.ProviderSessionId);

        using var retryClient = fixture.CreateOrdersClient();
        var retry = await retryClient.PostAsJsonAsync("/payments", new { orderId, amount = 9.99m, returnUrl = "http://shop.test/orders" }, Ct);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var payment = (await retry.Content.ReadFromJsonAsync<PaymentResponse>(Ct))!;
        Assert.Equal(kept.Id, payment.Id);
        Assert.NotNull(payment.CheckoutUrl);
    }

    [Fact]
    public async Task A_missing_order_id_is_a_validation_problem()
    {
        using var client = fixture.CreateOrdersClient();

        var response = await client.PostAsJsonAsync("/payments", new { orderId = Guid.Empty, amount = 0, returnUrl = "nope" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
