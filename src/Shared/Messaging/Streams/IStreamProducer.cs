namespace NewStore.Messaging;

/// <summary>
/// Produces a stream message straight to Kafka, without the outbox (AGENTS.md, Streams; ADR 0022). Use it only for a
/// high-frequency message that the next one replaces and that the producer can make again from its own state.
/// </summary>
public interface IStreamProducer
{
    /// <summary>
    /// Sends <paramref name="message"/> as JSON with System.Text.Json's defaults (as the outbox does), with the header
    /// <c>type</c> = <typeparamref name="TMessage"/>'s full name and <c>traceparent</c> when an activity is current.
    /// Completes when Kafka acknowledged the message; throws <see cref="Confluent.Kafka.ProduceException{TKey,TValue}"/>
    /// when it did not within <c>Messaging:ProduceTimeout</c>. Uses the service's one idempotent, <c>acks=all</c> producer.
    /// </summary>
    Task ProduceAsync<TMessage>(string topic, string key, TMessage message, CancellationToken cancellationToken)
        where TMessage : class;
}
