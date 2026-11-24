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
public sealed class MessagingCleanupService<TContext>(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    TimeProvider timeProvider,
    ILogger<MessagingCleanupService<TContext>> logger) : BackgroundService
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
                    var result = await MessagingCleanup.CleanAsync(context, settings, timeProvider.GetUtcNow(), stoppingToken);

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
}

/// <summary>The cleanup pass itself, on a non-generic type so a test can call it without the hosted service.</summary>
public static class MessagingCleanup
{
    /// <summary>
    /// One pass: a <c>DELETE</c> per table. Takes <paramref name="now"/> so a test needs no timer. A table that is not in the
    /// context's model is skipped: a service that consumes nothing (Inventory) has an outbox and no inbox.
    /// </summary>
    public static async Task<CleanupResult> CleanAsync(
        DbContext db, MessagingOptions options, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var outboxCutoff = now - options.OutboxRetention;
        var inboxCutoff = now - options.InboxRetention;

        var outbox = Has<OutboxMessage>(db)
            ? await db.Set<OutboxMessage>()
                .Where(m => m.PublishedAt != null && m.PublishedAt < outboxCutoff)
                .ExecuteDeleteAsync(ct)
            : 0;
        var inbox = Has<InboxMessage>(db)
            ? await db.Set<InboxMessage>()
                .Where(m => m.HandledAt < inboxCutoff)
                .ExecuteDeleteAsync(ct)
            : 0;

        return new CleanupResult(outbox, inbox);
    }

    // Set<T>() on a type the model lacks throws "Cannot create a DbSet for 'T' because this type is not included in the model".
    private static bool Has<TEntity>(DbContext db) => db.Model.FindEntityType(typeof(TEntity)) is not null;
}
