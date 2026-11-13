using Confluent.Kafka;
using Confluent.Kafka.Admin;

using NewStore.Messaging;
using NewStore.Messaging.Kafka;

using Testcontainers.Kafka;

namespace NewStore.TestSupport;

/// <summary>
/// One Kafka container per test assembly, like <see cref="PostgresServer"/>: register it with <c>[assembly: AssemblyFixture(typeof(KafkaServer))]</c>. A class that pauses the broker uses a subclass as a
/// class fixture, so other tests keep their broker.
/// </summary>
public class KafkaServer : IAsyncLifetime
{
    private readonly KafkaContainer _container = new KafkaBuilder("apache/kafka:4.4.0").Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Freezes the broker's processes (<c>docker pause</c>): connections stay open and nothing answers.</summary>
    public Task PauseAsync(CancellationToken cancellationToken) => _container.PauseAsync(cancellationToken);

    public Task UnpauseAsync(CancellationToken cancellationToken) => _container.UnpauseAsync(cancellationToken);

    public async Task CreateTopicAsync(string name, int partitions)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = name, NumPartitions = partitions, ReplicationFactor = 1 }]);
    }

    /// <summary>
    /// Publishes <paramref name="event"/> the way the outbox relay does: the JSON payload, and the <c>message-id</c>, <c>type</c> and
    /// <c>occurred-at</c> headers, so a service's consumer accepts it. Publishing the same event twice sends the same message id
    /// twice, which is how a test makes Kafka deliver a duplicate.
    /// </summary>
    public async Task PublishAsync<TEvent>(string topic, string key, TEvent @event, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = BootstrapServers }).Build();
        var message = new Message<string, string>
        {
            Key = key,
            Value = System.Text.Json.JsonSerializer.Serialize(@event),
            Headers = [],
        };
        message.Headers.Add(MessageHeaders.MessageId, System.Text.Encoding.UTF8.GetBytes(@event.EventId.ToString()));
        message.Headers.Add(MessageHeaders.Type, System.Text.Encoding.UTF8.GetBytes(typeof(TEvent).FullName!));
        message.Headers.Add(
            MessageHeaders.OccurredAt,
            System.Text.Encoding.UTF8.GetBytes(@event.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        await producer.ProduceAsync(topic, message, cancellationToken);
    }
}
