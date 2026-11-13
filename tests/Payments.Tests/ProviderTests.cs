using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NewStore.Messaging;
using NewStore.Payments.Payments;
using NewStore.Payments.Provider;

using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NewStore.Payments.Tests;

/// <summary>
/// The provider client sits behind Polly's resilience pipeline. When the circuit is open or a timeout fires, Polly throws its own
/// exceptions, which Messaging does not know. The client turns them into <see cref="TransientFailureException"/>, so a handler
/// that calls it is retried instead of dead-lettered.
/// </summary>
public class ProviderTests
{
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static FakePayProvider ProviderThatThrows(Exception exception)
    {
        var client = new HttpClient(new ThrowingHandler(exception)) { BaseAddress = new Uri("http://fakepay.test") };
        return new FakePayProvider(
            client, new FixedClientFactory(client), Options.Create(new PaymentsOptions()), TimeProvider.System, NullLogger<FakePayProvider>.Instance);
    }

    private static PaymentAttempt Attempt() => new() { Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), Amount = 5m, ProviderSessionId = "cs_1" };

    public static TheoryData<Exception> PollyRejections => new()
    {
        new BrokenCircuitException("The circuit is open."),
        new TimeoutRejectedException("The attempt timed out."),
    };

    [Theory]
    [MemberData(nameof(PollyRejections))]
    public async Task Creating_a_session_turns_a_Polly_rejection_into_a_transient_failure(Exception rejection)
    {
        var provider = ProviderThatThrows(rejection);

        var thrown = await Assert.ThrowsAsync<TransientFailureException>(() =>
            provider.CreateSessionAsync(Attempt(), "http://shop.test/ok", "http://shop.test/no", TestContext.Current.CancellationToken));

        Assert.Same(rejection, thrown.InnerException);
        Assert.True(TransientFailures.IsTransient(thrown));
    }

    [Theory]
    [MemberData(nameof(PollyRejections))]
    public async Task Refunding_turns_a_Polly_rejection_into_a_transient_failure(Exception rejection)
    {
        var provider = ProviderThatThrows(rejection);

        var thrown = await Assert.ThrowsAsync<TransientFailureException>(() =>
            provider.RefundAsync(Attempt(), TestContext.Current.CancellationToken));

        Assert.Same(rejection, thrown.InnerException);
    }

    [Fact]
    public void A_signed_event_of_an_unknown_type_is_refused_and_logged_as_a_warning()
    {
        var capture = new LogCapture();
        using var loggers = LoggerFactory.Create(logging => logging.AddProvider(capture));
        var options = Options.Create(new PaymentsOptions { Provider = { WebhookSecret = TestWebhooks.Secret } });
        var provider = new FakePayProvider(
            new HttpClient(), new FixedClientFactory(new HttpClient()), options, TimeProvider.System, loggers.CreateLogger<FakePayProvider>());
        var body = TestWebhooks.Body("evt_1", "checkout.session.something_new", "cs_1");
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary
        {
            ["FakePay-Signature"] = TestWebhooks.Sign(body, DateTimeOffset.UtcNow),
        };

        var read = provider.ReadWebhook(body, headers);

        Assert.Null(read);
        var warning = Assert.Single(capture.Snapshot(), l => l.Level == LogLevel.Warning);
        Assert.Contains("checkout.session.something_new", warning.Message, StringComparison.Ordinal);
    }
}
