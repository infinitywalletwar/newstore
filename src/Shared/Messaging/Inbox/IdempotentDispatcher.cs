using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NewStore.Messaging.Inbox;

/// <summary>
/// Runs a handler at most once per message id. In one database transaction it inserts the inbox row, runs
/// the handler and commits. The handler's writes and the inbox row therefore commit together or not at all:
/// a crash in between leaves neither, and the redelivered message runs the handler again from a clean state.
/// </summary>
internal sealed class IdempotentDispatcher<TContext>(
    IServiceScopeFactory scopeFactory, IOptions<MessagingOptions> options, TimeProvider timeProvider)
    where TContext : DbContext
{
    /// <summary>Returns false when the message was handled before and nothing ran.</summary>
    public async Task<bool> DispatchAsync(
        HandlerRegistration handler, Guid messageId, string payload, CancellationToken cancellationToken)
    {
        // A scope per attempt: a context that saw a failed handler must not be reused.
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var consumer = options.Value.ClientId!;
        var now = timeProvider.GetUtcNow();

        // The primary key (consumer, message_id) makes a second insert of the same message do nothing, and
        // two consumers racing on one message cannot both get a row: the second waits for the first's commit.
        var inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO inbox_messages (consumer, message_id, handled_at)
            VALUES ({consumer}, {messageId}, {now})
            ON CONFLICT DO NOTHING
            """,
            cancellationToken);

        if (inserted == 0)
        {
            return false;
        }

        await handler.Invoke(scope.ServiceProvider, payload, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
