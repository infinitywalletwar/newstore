using Microsoft.Extensions.Options;

using NewStore.FakePay.Webhooks;

namespace NewStore.FakePay.Sessions;

/// <summary>Expires open sessions past their <c>expiresAt</c> and queues <c>checkout.session.expired</c> for each.</summary>
public sealed class ExpirySweeper(
    SessionStore store, WebhookSender webhooks, IOptions<FakePayOptions> options, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The timer takes the TimeProvider, so a test moves the sweeper by advancing a fake clock.
        using var timer = new PeriodicTimer(options.Value.ExpirySweepInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var session in store.ExpireDue(time.GetUtcNow()))
            {
                webhooks.Enqueue(WebhookEvent.For(WebhookTypes.SessionExpired, session.Id));
            }
        }
    }
}
