using System.Diagnostics;
using System.Text.Json;

using Confluent.Kafka;

using NewStore.Messaging.Kafka;

namespace NewStore.Messaging.Streams;

/// <summary>
/// <see cref="IStreamProducer"/> over the service's one <see cref="KafkaProducer"/>: idempotent, <c>acks=all</c>, and
/// <c>MessageTimeoutMs</c> = <see cref="MessagingOptions.ProduceTimeout"/>. A stream message has no <c>message-id</c>: no
/// inbox reads it, and the handler dedupes by the message's own natural key.
/// </summary>
internal sealed class StreamProducer(KafkaProducer producer) : IStreamProducer
{
    public async Task ProduceAsync<TMessage>(string topic, string key, TMessage message, CancellationToken cancellationToken)
        where TMessage : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(message);

        var kafkaMessage = new Message<string, string>
        {
            Key = key,
            // The static type TMessage, as the outbox does: the consumer deserialises into the same type.
            Value = JsonSerializer.Serialize(message),
            Headers = [],
        };
        kafkaMessage.Headers.Add(MessageHeaders.Type, typeof(TMessage).FullName!);
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
        {
            kafkaMessage.Headers.Add(MessageHeaders.TraceParent, traceParent);
        }

        await producer.ProduceAsync(topic, kafkaMessage, cancellationToken);
    }
}
