using NewStore.Messaging;
using NewStore.Orders.Clients;

using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace NewStore.Orders.Tests;

/// <summary>
/// The clients turn every "no answer" into <see cref="TransientFailureException"/>. Polly throws a subclass of
/// <see cref="Polly.ExecutionRejectedException"/> when its pipeline gives up (<see cref="BrokenCircuitException"/>,
/// <see cref="TimeoutRejectedException"/> or <see cref="RateLimiterRejectedException"/>), and Messaging does not know them. These tests put the exception
/// where the pipeline would throw it: below the client, in its message handler.
/// </summary>
public class ClientTests
{
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static HttpClient ClientThatThrows(Exception exception) =>
        new(new ThrowingHandler(exception)) { BaseAddress = new Uri("http://inventory.test") };

    public static TheoryData<Exception> NoAnswer => new()
    {
        new BrokenCircuitException("The circuit is open."),
        new TimeoutRejectedException("The attempt timed out."),
        // The standard handler's concurrency limiter rejects with this third subclass of ExecutionRejectedException.
        new RateLimiterRejectedException(),
        new HttpRequestException("Connection refused."),
    };

    [Theory]
    [MemberData(nameof(NoAnswer))]
    public async Task Inventory_calls_that_get_no_answer_throw_a_transient_failure(Exception exception)
    {
        var client = new InventoryClient(ClientThatThrows(exception));
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TransientFailureException>(() => client.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<TransientFailureException>(() => client.HoldAsync(Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<TransientFailureException>(() => client.ConfirmAsync(Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<TransientFailureException>(() => client.ReleaseAsync(Guid.NewGuid(), ct));
    }

    [Theory]
    [MemberData(nameof(NoAnswer))]
    public async Task Payments_calls_that_get_no_answer_throw_a_transient_failure(Exception exception)
    {
        var client = new PaymentsClient(ClientThatThrows(exception));

        await Assert.ThrowsAsync<TransientFailureException>(
            () => client.CreateAsync(Guid.NewGuid(), 9.99m, "http://shop.test/orders/1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_call_is_not_a_transient_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = new InventoryClient(ClientThatThrows(new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.HoldAsync(Guid.NewGuid(), cts.Token));
    }
}
