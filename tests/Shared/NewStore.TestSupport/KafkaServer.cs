using Confluent.Kafka;
using Confluent.Kafka.Admin;

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
}
