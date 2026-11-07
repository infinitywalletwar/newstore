using System.Diagnostics;
using System.Text;

using Confluent.Kafka;

using NewStore.Contracts.Inventory;
using NewStore.Messaging.Kafka;

namespace NewStore.Inventory.Tests;

/// <summary>
/// The whole path: edition created through the API, snapshot loop, outbox, relay, Kafka. The fixture's clock is a
/// <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>, so neither the snapshot loop's timer nor the
/// relay's delay moves until the test advances it. It does that in small steps while it waits for the message.
/// This is a class of its own, so the clock steps cannot start a loop pass inside the tests of <see cref="SnapshotTests"/>.
/// </summary>
public sealed class SnapshotKafkaTests(ConditionalUpdateFixture fixture, KafkaServer kafka) : IClassFixture<ConditionalUpdateFixture>
{
    [Fact]
    public async Task Snapshot_reaches_Kafka()
    {
        using var client = fixture.CreateClient();
        var edition = await client.CreateEditionAsync();

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            GroupId = $"snapshot-test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        consumer.Subscribe(EditionSnapshot.Topic);

        var key = edition.Id.ToString();
        ConsumeResult<string, string>? received = null;
        var waited = Stopwatch.StartNew();
        while (received is null && waited.Elapsed < TimeSpan.FromSeconds(15))
        {
            // One step makes the snapshot loop and the relay run once each.
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            var result = consumer.Consume(TimeSpan.FromMilliseconds(300));
            if (result?.Message.Key == key)
            {
                received = result;
            }
        }

        Assert.NotNull(received);
        Assert.Equal(
            typeof(EditionSnapshot).FullName,
            Encoding.UTF8.GetString(received.Message.Headers.GetLastBytes(MessageHeaders.Type)));
    }
}
