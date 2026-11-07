using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NewStore.Messaging.Inbox;
using NewStore.Messaging.Outbox;

namespace NewStore.Messaging.Cleanup;

/// <summary>How many rows one cleanup pass deleted from each table.</summary>
public readonly record struct CleanupResult(int OutboxDeleted, int InboxDeleted);

/// <summary>
/// Keeps <c>outbox_messages</c> and <c>inbox_messages</c> from growing without limit. Every
/// <see cref="MessagingOptions.CleanupInterval"/> it deletes published outbox rows older than
/// <see cref="MessagingOptions.OutboxRetention"/> and inbox rows older than <see cref="MessagingOptions.InboxRetention"/>.
/// An unpublished outbox row is never deleted, however old: it is still waiting for Kafka.
/// </summary>
public sealed class MessagingCleanup<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessagingCleanup<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        // The first pass runs after the first interval, not at startup: a service that is starting has other work.
        using var timer = new PeriodicTimer(settings.CleanupInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<TContext>();
                    var result = await CleanAsync(context, settings, timeProvider.GetUtcNow(), stoppingToken);

                    if (result.OutboxDeleted + result.InboxDeleted > 0)
                    {
                        MessagingLog.CleanupDeleted(logger, result.OutboxDeleted, result.InboxDeleted);
                    }
                    else
                    {
                        MessagingLog.CleanupNothingToDelete(logger);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The database may be restarting. The loop must outlive that.
                    MessagingLog.CleanupFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown while waiting for the next tick.
        }
    }

    /// <summary>One pass: two <c>DELETE</c> statements, one per table. Takes <paramref name="now"/> so a test needs no timer.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
        Justification = "The pass needs TContext and is called from tests without constructing the service.")]
    public static async Task<CleanupResult> CleanAsync(
        TContext db, MessagingOptions options, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var outboxCutoff = now - options.OutboxRetention;
        var inboxCutoff = now - options.InboxRetention;

        var outbox = await db.Set<OutboxMessage>()
            .Where(m => m.PublishedAt != null && m.PublishedAt < outboxCutoff)
            .ExecuteDeleteAsync(ct);
        var inbox = await db.Set<InboxMessage>()
            .Where(m => m.HandledAt < inboxCutoff)
            .ExecuteDeleteAsync(ct);

        return new CleanupResult(outbox, inbox);
    }
}
