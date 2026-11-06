namespace NewStore.Messaging;

public interface IOutbox
{
    /// <summary>
    /// Adds an outbox row to the current context; the caller's <c>SaveChangesAsync</c> commits it with the
    /// business rows. <paramref name="key"/> is the Kafka partition key: use the aggregate's id, so one
    /// aggregate's events stay in order.
    /// </summary>
    void Enqueue<TEvent>(TEvent integrationEvent, string topic, string key)
        where TEvent : IIntegrationEvent;
}
