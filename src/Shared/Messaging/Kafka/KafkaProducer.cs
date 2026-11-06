using Confluent.Kafka;

using Microsoft.Extensions.Options;

namespace NewStore.Messaging.Kafka;

/// <summary>
/// The service's one Kafka producer. It is idempotent and waits for all in-sync replicas (<c>Acks.All</c>), so a
/// retry inside the client neither duplicates nor reorders a message.
/// </summary>
internal sealed class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaProducer(IOptions<MessagingOptions> options)
    {
        var settings = options.Value;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = settings.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = (int)settings.ProduceTimeout.TotalMilliseconds,
        }).Build();
    }

    /// <summary>Completes when Kafka acknowledged the message; throws <see cref="ProduceException{TKey,TValue}"/> when it did not.</summary>
    public Task<DeliveryResult<string, string>> ProduceAsync(
        string topic, Message<string, string> message, CancellationToken cancellationToken) =>
        _producer.ProduceAsync(topic, message, cancellationToken);

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
