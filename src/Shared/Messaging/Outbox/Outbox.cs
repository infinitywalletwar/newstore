using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

namespace NewStore.Messaging.Outbox;

/// <summary>
/// Adds the outbox row to the caller's <typeparamref name="TContext"/> without saving. The caller's
/// <c>SaveChangesAsync</c> then commits the row and the business rows in one transaction, so either both
/// exist or neither does.
/// </summary>
internal sealed class Outbox<TContext>(TContext context) : IOutbox
    where TContext : DbContext
{
    public void Enqueue<TEvent>(TEvent integrationEvent, string topic, string key)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        context.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Topic = topic,
            Key = key,
            Type = typeof(TEvent).FullName!,
            // The static type TEvent, not the runtime type: the consumer deserialises into the same TEvent.
            Payload = JsonSerializer.Serialize(integrationEvent),
            OccurredAt = integrationEvent.OccurredAt,
            TraceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null,
        });
    }
}
