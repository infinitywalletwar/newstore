using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using NewStore.TestSupport;

namespace NewStore.Payments.Tests;

/// <summary>
/// Stands in for FakePay's API. It is idempotent like the real one: the session id is made from the <c>Idempotency-Key</c>, so
/// the same key gives the same session. A test changes <see cref="CreateSession"/> or <see cref="Refund"/> to make the provider fail.
/// </summary>
internal sealed class FakePayStub
{
    public FakePayStub()
    {
        Handler = new StubHttpHandler()
            .On(HttpMethod.Post, "/v1/checkout/sessions", request => CreateSession(request))
            .On(HttpMethod.Post, "/v1/refunds", request => Refund(request));
    }

    public StubHttpHandler Handler { get; }

    /// <summary>When true, session requests never answer, until the caller gives up. Refunds are not affected.</summary>
    public bool HangSessions { get; set; }

    /// <summary>What goes below the clients' resilience pipelines: the stub, behind a handler that can hang a session request.</summary>
    public HttpMessageHandler Primary => field ??= new HangableHandler(this) { InnerHandler = Handler };

    private sealed class HangableHandler(FakePayStub owner) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner.HangSessions && request.RequestUri!.AbsolutePath == "/v1/checkout/sessions")
            {
                // Only the caller's token ends this wait: the pipeline's timeout cancels it.
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    public Func<HttpRequestMessage, HttpResponseMessage> CreateSession { get; set; } = request =>
    {
        var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
        // Slow enough that parallel callers overlap: a lock that does not work shows up as several session requests.
        Thread.Sleep(150);
        var key = request.Headers.GetValues("Idempotency-Key").Single();
        return Json(new
        {
            id = $"cs_{key}",
            url = $"http://fakepay.test/checkout/cs_{key}",
            status = "open",
            expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset(),
        });
    };

    public Func<HttpRequestMessage, HttpResponseMessage> Refund { get; set; } = request =>
        Json(new { id = $"re_{request.Headers.GetValues("Idempotency-Key").Single()}", status = "succeeded" });

    /// <summary>The requests to <c>/v1/checkout/sessions</c> whose metadata names <paramref name="orderId"/>.</summary>
    public IReadOnlyList<HttpRequestMessage> SessionRequestsFor(Guid orderId) =>
        [.. Handler.Requests.Where(r => r.RequestUri!.AbsolutePath == "/v1/checkout/sessions"
            && r.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains(orderId.ToString(), StringComparison.Ordinal))];

    public IReadOnlyList<HttpRequestMessage> RefundRequestsFor(string sessionId) =>
        [.. Handler.Requests.Where(r => r.RequestUri!.AbsolutePath == "/v1/refunds"
            && r.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains(sessionId, StringComparison.Ordinal))];

    public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
