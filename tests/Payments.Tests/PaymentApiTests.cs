using System.Globalization;
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
        var before = DateTimeOffset.UtcNow;

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

        // The session is asked for 30 minutes plus a margin. The provider counts its 30-minute minimum from when the request arrives,
        // so "exactly 30 minutes from the moment we built the request" would be refused.
        var requestedExpiry = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(Ct))
            .RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(requestedExpiry - before, TimeSpan.FromSeconds(30 * 60 + 20), TimeSpan.FromMinutes(31));

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

    [Theory]
    [InlineData("100000000")]
    [InlineData("99999999.995")] // rounds to 100000000.00 in numeric(10,2)
    public async Task An_amount_that_does_not_fit_the_column_is_400(string amount)
    {
        var orderId = Guid.NewGuid();
        using var client = fixture.CreateOrdersClient();
        using var content = new StringContent(
            $$"""{"orderId":"{{orderId}}","amount":{{amount}},"returnUrl":"http://shop.test/orders"}""",
            System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/payments", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await fixture.QueryAsync(db => db.PaymentAttempts.CountAsync(a => a.OrderId == orderId, Ct)));
    }

    [Fact]
    public async Task The_amount_error_names_the_maximum_with_a_decimal_point_whatever_the_servers_culture()
    {
        // The test server does not carry the test's own culture into the request (it runs the app without the caller's execution
        // context), so the culture is set for the threads that run the request.
        var previous = CultureInfo.DefaultThreadCurrentCulture;
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("de-DE");
        try
        {
            using var client = fixture.CreateOrdersClient();
            using var content = new StringContent(
                $$"""{"orderId":"{{Guid.NewGuid()}}","amount":100000000,"returnUrl":"http://shop.test/orders"}""",
                System.Text.Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/payments", content, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
            Assert.Equal(
                "amount must not be more than 99999999.99.",
                problem.GetProperty("errors").GetProperty("amount")[0].GetString());
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = previous;
        }
    }

    [Fact]
    public async Task The_largest_amount_that_fits_the_column_is_accepted()
    {
        var payment = await fixture.CreatePaymentAsync(amount: 99_999_999.99m);

        Assert.Equal(99_999_999.99m, payment.Amount);
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
    public async Task A_provider_that_never_answers_gives_503_within_the_session_budget_and_frees_the_lock()
    {
        // The shipped options: the fixture trims only the refund client's pipeline. Session creation holds a row lock and a
        // transaction, so its pipeline must give up well inside Orders' 10 s attempt timeout (the standard handler's default).
        var orderId = Guid.NewGuid();
        fixture.Provider.HangSessions = true;
        try
        {
            using var client = fixture.CreateOrdersClient();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var response = await client.PostAsJsonAsync("/payments", new { orderId, amount = 9.99m, returnUrl = "http://shop.test/orders" }, Ct);

            watch.Stop();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.RetryAfter is not null);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"The 503 took {watch.Elapsed}.");
        }
        finally
        {
            fixture.Provider.HangSessions = false;
        }

        // The lock is gone: the next call for the same order does not wait, and finishes the payment.
        using var retryClient = fixture.CreateOrdersClient();
        var retry = await retryClient.PostAsJsonAsync("/payments", new { orderId, amount = 9.99m, returnUrl = "http://shop.test/orders" }, Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task A_missing_order_id_is_a_validation_problem()
    {
        using var client = fixture.CreateOrdersClient();

        var response = await client.PostAsJsonAsync("/payments", new { orderId = Guid.Empty, amount = 0, returnUrl = "nope" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
