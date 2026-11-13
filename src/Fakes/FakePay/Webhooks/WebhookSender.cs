using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Options;

namespace NewStore.FakePay.Webhooks;

public static class WebhookTypes
{
    public const string SessionCompleted = "checkout.session.completed";
    public const string SessionExpired = "checkout.session.expired";
}

public sealed record WebhookEvent(string Id, string Type, string SessionId)
{
    public static WebhookEvent For(string type, string sessionId) => new($"evt_{Guid.NewGuid():N}", type, sessionId);

    /// <summary><c>{ "id": "evt_…", "type": "…", "data": { "sessionId": "…" } }</c></summary>
    public string ToJson() => JsonSerializer.Serialize(new { id = Id, type = Type, data = new { sessionId = SessionId } });
}

/// <summary>
/// Delivers queued webhooks. Each event has its own delivery loop, so a receiver that refuses one event does not hold up the
/// others. A loop posts the event, waits <see cref="FakePayOptions.WebhookRetryInterval"/> after each failure and stops on the
/// first 2xx. With <see cref="FakePayOptions.DeliverTwice"/> it then posts the same event, with the same id, a second time. Every
/// attempt is signed afresh, so the timestamp is the time of that attempt.
/// </summary>
public sealed class WebhookSender(
    IServiceScopeFactory scopes, IOptions<FakePayOptions> options, TimeProvider time) : BackgroundService
{
    private readonly Channel<WebhookEvent> _queue = Channel.CreateUnbounded<WebhookEvent>();

    public void Enqueue(WebhookEvent webhookEvent) => _queue.Writer.TryWrite(webhookEvent);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var webhookEvent in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _ = DeliverAsync(webhookEvent, stoppingToken);
        }
    }

    private async Task DeliverAsync(WebhookEvent webhookEvent, CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var body = webhookEvent.ToJson();
        var deliveries = settings.DeliverTwice ? 2 : 1;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<WebhookClient>();
            for (var delivery = 0; delivery < deliveries; delivery++)
            {
                while (true)
                {
                    var signature = WebhookSigner.Sign(settings.WebhookSecret, body, time.GetUtcNow());
                    if (await client.PostAsync(new Uri(settings.WebhookUrl), body, signature, stoppingToken))
                    {
                        break;
                    }

                    // Real time, not TimeProvider: a test sets a short interval and the fake clock is for expiry only.
                    await Task.Delay(settings.WebhookRetryInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: undelivered webhooks are lost, which is fine for a stand-in kept in memory.
        }
    }
}
