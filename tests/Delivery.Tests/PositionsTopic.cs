using System.Text.Json;

using Confluent.Kafka;

using NewStore.Delivery.Tracking;

namespace NewStore.Delivery.Tests;

/// <summary>
/// Reads <c>delivery.positions</c> from the start with a plain consumer. It takes every partition itself with <c>Assign</c>, where
/// <c>Subscribe</c> would leave the choice to the consumer group, and it commits nothing, so each read sees every copy of every
/// message, as Kafka stores them.
/// </summary>
public sealed class PositionsTopic(string bootstrapServers)
{
    /// <summary>Every message for <paramref name="shipmentId"/> on the topic now, in the order its partition holds them.</summary>
    /// <remarks>
    /// Under load (every test project at once) the broker once answered the offset query with "Not leader for partition", so a
    /// read that fails with that error, or with "Leader not available", is tried again until 30 s have passed since the first
    /// attempt. Any other error fails the test at once.
    /// </remarks>
    public async Task<IReadOnlyList<CourierPosition>> ReadAsync(Guid shipmentId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return await Task.Run(() => Read(shipmentId), ct);
            }
            catch (KafkaException ex) when (
                ex.Error.Code is ErrorCode.NotLeaderForPartition or ErrorCode.LeaderNotAvailable && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
            }
        }
    }

    /// <summary>Reads until <paramref name="done"/> holds for the shipment's messages; fails after <paramref name="timeout"/> (30 s).</summary>
    public async Task<IReadOnlyList<CourierPosition>> WaitForAsync(
        Guid shipmentId, Func<IReadOnlyList<CourierPosition>, bool> done, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        IReadOnlyList<CourierPosition> positions = [];
        while (DateTime.UtcNow < deadline)
        {
            positions = await ReadAsync(shipmentId);
            if (done(positions))
            {
                return positions;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for: {what}. Seqs on the topic: [{string.Join(", ", positions.Select(p => p.Seq))}].");
        return positions;
    }

    private List<CourierPosition> Read(Guid shipmentId)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = $"test-positions-{Guid.NewGuid():N}",   // its own, so no other reader's offsets apply
            EnableAutoCommit = false,
        }).Build();

        // Each partition's end now; the read stops when it has reached every one of them.
        var ends = Enumerable.Range(0, CourierPosition.Partitions)
            .Select(p => new TopicPartition(CourierPosition.Topic, p))
            .ToDictionary(tp => tp.Partition.Value, tp => consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(10)).High.Value);
        var next = ends.Keys.ToDictionary(p => p, _ => 0L);
        consumer.Assign(ends.Keys.Select(p => new TopicPartitionOffset(CourierPosition.Topic, p, Offset.Beginning)));

        var positions = new List<CourierPosition>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (ends.Any(e => next[e.Key] < e.Value))
        {
            if (DateTime.UtcNow >= deadline)
            {
                // A partial list would read as "the message isn't there", so a read that can't reach the end fails instead.
                var behind = ends.Where(e => next[e.Key] < e.Value).Select(e => $"partition {e.Key} at {next[e.Key]} of {e.Value}");
                throw new TimeoutException($"Reading {CourierPosition.Topic} didn't reach the end in 10 s: {string.Join(", ", behind)}.");
            }

            if (consumer.Consume(TimeSpan.FromMilliseconds(200)) is not { Message: not null } result)
            {
                continue;
            }

            next[result.Partition.Value] = result.Offset.Value + 1;
            if (result.Message.Key == shipmentId.ToString())
            {
                positions.Add(JsonSerializer.Deserialize<CourierPosition>(result.Message.Value)!);
            }
        }

        consumer.Close();
        return positions;
    }
}
