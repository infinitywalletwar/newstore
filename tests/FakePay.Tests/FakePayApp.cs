using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using NewStore.FakePay.Webhooks;

namespace NewStore.FakePay.Tests;

/// <summary>
/// FakePay with a stub where Payments would be, and a clock the test moves. One app per test, because the options differ:
/// <c>DeliverTwice</c> and the test endpoints are settings of the app, not of a request.
/// </summary>
internal sealed class FakePayApp : NewStoreAppFactory<Program>
{
    public const string ApiKey = "test-key";
    public const string WebhookSecret = "test-secret";

    public FakePayApp(bool deliverTwice = false, bool enableTestEndpoints = false, string sweepInterval = "00:00:01")
    {
        Settings["FakePay:ApiKey"] = ApiKey;
        Settings["FakePay:WebhookSecret"] = WebhookSecret;
        Settings["FakePay:WebhookUrl"] = "http://payments.test/webhooks/fakepay";
        Settings["FakePay:PublicBaseUrl"] = "http://fakepay.test";
        Settings["FakePay:WebhookRetryInterval"] = "00:00:00.050";
        Settings["FakePay:ExpirySweepInterval"] = sweepInterval;
        Settings["FakePay:DeliverTwice"] = deliverTwice.ToString();
        Settings["FakePay:EnableTestEndpoints"] = enableTestEndpoints.ToString();
        Receiver.On(HttpMethod.Post, "/webhooks/fakepay", request => ReceiverReply(request));
    }

    /// <summary>What the receiver answers. A test replaces it to make the receiver fail first.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> ReceiverReply { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    /// <summary>Stands in for Payments' webhook endpoint and records what it receives. Its answer is <see cref="ReceiverReply"/>.</summary>
    public StubHttpHandler Receiver { get; } = new();

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.UseStubHandler<WebhookClient>(Receiver);
        });
    }

    public HttpClient CreateApiClient(string? apiKey = ApiKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return apiKey is null ? client : client.WithBearer(apiKey);
    }

    public async Task<SessionDto> CreateSessionAsync(
        HttpClient client, string? idempotencyKey = null, TimeSpan? lifetime = null, string successUrl = "http://shop.test/done")
    {
        var response = await PostSessionAsync(client, idempotencyKey ?? Guid.NewGuid().ToString(), lifetime ?? TimeSpan.FromMinutes(30), successUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionDto>(TestContext.Current.CancellationToken))!;
    }

    public async Task<HttpResponseMessage> PostSessionAsync(
        HttpClient client, string idempotencyKey, TimeSpan lifetime, string successUrl = "http://shop.test/done")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/sessions")
        {
            Content = JsonContent.Create(new
            {
                amount = 12.50m,
                successUrl,
                cancelUrl = "http://shop.test/cancel",
                expiresAt = Clock.GetUtcNow() + lifetime,
                metadata = new { orderId = "o-1" },
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The webhook bodies the stub received, parsed, in order.</summary>
    public IReadOnlyList<ReceivedWebhook> Webhooks() =>
        [.. Receiver.Requests.Select(r => ReceivedWebhook.From(r))];

    /// <summary>Waits until the stub has received <paramref name="count"/> webhooks.</summary>
    public async Task<IReadOnlyList<ReceivedWebhook>> WaitForWebhooksAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (Receiver.Requests.Count >= count)
            {
                return Webhooks();
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Expected {count} webhooks within 10 s, got {Receiver.Requests.Count}.");
        return [];
    }
}

public sealed record SessionDto(string Id, string Url, string Status, DateTimeOffset ExpiresAt);

internal sealed record ReceivedWebhook(string Id, string Type, string SessionId, string Body, string? Signature)
{
    public static ReceivedWebhook From(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new ReceivedWebhook(
            root.GetProperty("id").GetString()!,
            root.GetProperty("type").GetString()!,
            root.GetProperty("data").GetProperty("sessionId").GetString()!,
            body,
            request.Headers.TryGetValues(WebhookSigner.HeaderName, out var values) ? values.Single() : null);
    }
}
