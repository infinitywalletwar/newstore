using System.Net;

using NewStore.Messaging;

using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace NewStore.Inventory.Tests;

/// <summary>
/// <see cref="CatalogClient"/> sits behind Polly's resilience pipeline. When the circuit is open or a timeout fires, Polly
/// throws its own exceptions, which the endpoint does not know. The client turns them into
/// <see cref="TransientFailureException"/>, as Payments' FakePay client does.
/// </summary>
public class CatalogClientTests
{
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static CatalogClient ClientThatThrows(Exception exception) =>
        new(new HttpClient(new ThrowingHandler(exception)) { BaseAddress = new Uri("http://catalog.test") });

    public static TheoryData<Exception> PollyRejections => new()
    {
        new BrokenCircuitException("The circuit is open."),
        new TimeoutRejectedException("The attempt timed out."),
        new RateLimiterRejectedException("The concurrency limiter rejected the request."),
    };

    [Theory]
    [MemberData(nameof(PollyRejections))]
    public async Task A_Polly_rejection_becomes_a_transient_failure(Exception rejection)
    {
        var client = ClientThatThrows(rejection);

        var thrown = await Assert.ThrowsAsync<TransientFailureException>(() =>
            client.GetVideoOwnerAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Same(rejection, thrown.InnerException);
    }

    [Fact]
    public async Task A_server_error_is_an_HttpRequestException()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, "/videos/{id}/owner", _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new CatalogClient(new HttpClient(stub) { BaseAddress = new Uri("http://catalog.test") });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetVideoOwnerAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_video_is_null()
    {
        var stub = new StubHttpHandler().On(HttpMethod.Get, "/videos/{id}/owner", _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new CatalogClient(new HttpClient(stub) { BaseAddress = new Uri("http://catalog.test") });

        Assert.Null(await client.GetVideoOwnerAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
