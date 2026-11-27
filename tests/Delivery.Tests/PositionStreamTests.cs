using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;

using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;
using NewStore.Messaging;

namespace NewStore.Delivery.Tests;

/// <summary>Delivery that leaves the positions stream alone, as host A of the two-host test does.</summary>
public sealed class NoPositionsFixture(PostgresServer postgres, KafkaServer kafka) : DeliveryFixture(postgres, kafka)
{
    protected override void ConfigureSettings(Dictionary<string, string?> settings) =>
        settings["Delivery:Tracking:ConsumePositions"] = "false";
}

public sealed class PositionStreamTests(DeliveryFixture delivery, PostgresServer postgres, KafkaServer kafka) : IClassFixture<DeliveryFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Positions_on_the_topic_reach_the_database()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var start = Api.Micro(delivery.Clock.GetUtcNow());
        var producer = delivery.Services.GetRequiredService<IStreamProducer>();

        foreach (var seq in new[] { 0, 1, 2 })
        {
            await producer.ProduceAsync(CourierPosition.Topic, shipment.Id.ToString(), Positions.At(shipment.Id, seq, start), Ct);
        }

        await AdvanceUntilAsync(delivery, async () => (await Positions.StoredSeqsAsync(delivery, shipment.Id)).Count == 3,
            "the three positions are stored");
        Assert.Equal([0, 1, 2], await Positions.StoredSeqsAsync(delivery, shipment.Id));
        Assert.Equal(2, (await Positions.ShipmentAsync(delivery, shipment.Id)).LastSeq);
    }

    [Fact]
    public async Task Consume_positions_off_leaves_the_topic_alone()
    {
        await using var off = new NoPositionsFixture(postgres, kafka);
        await off.InitializeAsync();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();

        // The event consumer joins its group, so the app is connected to Kafka; then the stream's group must still be missing.
        await DeliveryFixture.WaitAsync(
            async () => (await admin.ListConsumerGroupsAsync()).Valid.Any(g => g.GroupId == off.ClientId), "the event consumer's group");
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);

        var groups = await admin.ListConsumerGroupsAsync();
        Assert.DoesNotContain(groups.Valid, g => g.GroupId == $"{off.ClientId}.{CourierPosition.Topic}");
    }

    /// <summary>
    /// Polls like <see cref="DeliveryFixture.WaitAsync"/>, and moves the app's fake clock on by a batch window each time: the stream
    /// consumer closes a batch when <c>Messaging:StreamBatchWindow</c> has passed on the app's <see cref="TimeProvider"/>.
    /// </summary>
    internal static Task AdvanceUntilAsync(DeliveryFixture app, Func<Task<bool>> condition, string what, TimeSpan? timeout = null) =>
        DeliveryFixture.WaitAsync(async () =>
        {
            app.Clock.Advance(TimeSpan.FromMilliseconds(200));
            return await condition();
        }, what, timeout);
}
