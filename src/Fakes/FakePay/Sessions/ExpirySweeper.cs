using Microsoft.Extensions.Options;

using NewStore.FakePay.Webhooks;

namespace NewStore.FakePay.Sessions;

/// <summary>Expires open sessions past their <c>expiresAt</c> and queues <c>checkout.session.expired</c> for each.</summary>
public sealed class ExpirySweeper(
    SessionStore store, WebhookSender webhooks, IOptions<FakePayOptions> options, TimeProvider time) : BackgroundService
{
    // Created in the constructor, not in ExecuteAsync. In .NET 10 the whole of ExecuteAsync runs on a background thread
    // (https://learn.microsoft.com/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task), so a test that
    // advances the fake clock right after startup could do it before the timer exists, and the tick would be lost.
    // The timer takes the TimeProvider, so a test moves the sweeper by advancing a fake clock.
    private readonly PeriodicTimer _timer = new(options.Value.ExpirySweepInterval, time);

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var session in store.ExpireDue(time.GetUtcNow()))
            {
                webhooks.Enqueue(WebhookEvent.For(WebhookTypes.SessionExpired, session.Id));
            }
        }
    }
}
