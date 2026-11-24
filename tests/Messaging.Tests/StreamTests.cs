using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewStore.Messaging.Tests;

/// <summary>The stream message of these tests, as a courier position would be: replaced by the next one.</summary>
public sealed record Ping(int N);

/// <summary>Every batch the handler was called with, in order, including the calls that threw. A singleton per host.</summary>
public sealed class PingRecorder(int failingCalls = 0)
{
    private readonly ConcurrentQueue<IReadOnlyList<Ping>> _calls = new();

    public IReadOnlyList<IReadOnlyList<Ping>> Calls => [.. _calls];

    /// <summary>The batches of the calls that returned: all but the first <c>failingCalls</c>.</summary>
    public IReadOnlyList<IReadOnlyList<Ping>> Batches => [.. Calls.Skip(failingCalls)];

    public IReadOnlyList<Ping> Handled => [.. Batches.SelectMany(b => b)];

    internal void Record(IReadOnlyList<Ping> batch)
    {
        _calls.Enqueue([.. batch]);
        if (_calls.Count <= failingCalls)
        {
            throw new InvalidOperationException("The handler fails on purpose.");
        }
    }
}

public sealed class RecordingPingHandler(PingRecorder recorder) : IStreamHandler<Ping>
{
    public Task HandleAsync(IReadOnlyList<Ping> batch, CancellationToken cancellationToken)
    {
        recorder.Record(batch);
        return Task.CompletedTask;
    }
}

public class StreamTests(PostgresServer postgres, KafkaServer kafka)
{
    internal const int Partitions = 6;
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Stream_producer_awaits_the_ack_and_writes_the_type_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(postgres, kafka);
        using var host = await scenario.StartHostAsync();
        var producer = host.Services.GetRequiredService<IStreamProducer>();

        await producer.ProduceAsync(scenario.Topic, "shipment-1", new Ping(7), ct);

        // The broker has the message the moment ProduceAsync returns: its partitions' end offsets add up to one.
        Assert.Equal(1, EndOffsets(scenario).Sum());
        var message = Assert.Single(await scenario.ReadAsync(scenario.Topic, 1, s_wait));
        Assert.Equal("shipment-1", message.Message.Key);
        Assert.Equal(new Ping(7), JsonSerializer.Deserialize<Ping>(message.Message.Value));
        Assert.Equal(typeof(Ping).FullName, Scenario.Header(message, "type"));
        Assert.False(message.Message.Headers.TryGetLastBytes("message-id", out _), "a stream message has no message id");
    }

    [Fact]
    public async Task Stream_handler_gets_batches_up_to_the_batch_size()
    {
        var scenario = await CreateScenarioAsync(postgres, kafka);
        await SendAsync(scenario, Enumerable.Range(1, 450).Select(n => PingMessage(n)));
        var recorder = new PingRecorder();

        // All 450 wait in the topic, and the window is long: only the size (the default, 200) closes a batch.
        using var host = await StartConsumerAsync(scenario, recorder, o => o.StreamBatchWindow = TimeSpan.FromSeconds(2));
        await Scenario.EventuallyAsync(() => Task.FromResult(recorder.Handled.Count >= 450), s_wait, "450 pings are handled");

        Assert.All(recorder.Batches, b => Assert.InRange(b.Count, 1, 200));
        Assert.Equal(200, recorder.Batches.Max(b => b.Count));
        Assert.Equal(Enumerable.Range(1, 450), recorder.Handled.Select(p => p.N).Order());
    }

    [Fact]
    public async Task Stream_handler_gets_a_partial_batch_after_the_window()
    {
        var scenario = await CreateScenarioAsync(postgres, kafka);
        var recorder = new PingRecorder();
        using var host = await StartConsumerAsync(scenario, recorder);

        // Warm-up: once a ping is handled, the consumer has joined its group and holds the partitions.
        await ProduceAsync(host, scenario.Topic, [0]);
        await Scenario.EventuallyAsync(() => Task.FromResult(recorder.Handled.Count == 1), s_wait, "the warm-up ping is handled");

        await ProduceAsync(host, scenario.Topic, [1, 2, 3]);
        await Scenario.EventuallyAsync(
            () => Task.FromResult(recorder.Handled.Count == 4), TimeSpan.FromSeconds(2), "three pings, far below the size, are handled within 2 s");

        Assert.All(recorder.Batches, b => Assert.InRange(b.Count, 1, 3));
    }

    [Fact]
    public async Task Stream_uses_its_own_consumer_group_and_no_dead_letter_topic()
    {
        var scenario = await CreateScenarioAsync(postgres, kafka);
        var recorder = new PingRecorder();
        using var host = await StartConsumerAsync(scenario, recorder);
        await ProduceAsync(host, scenario.Topic, [1]);
        await Scenario.EventuallyAsync(() => Task.FromResult(recorder.Handled.Count == 1), s_wait, "the ping is handled");

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = scenario.BootstrapServers }).Build();
        var groups = await admin.ListConsumerGroupsAsync();
        Assert.Contains(groups.Valid, g => g.GroupId == $"{scenario.ClientId}.{scenario.Topic}");
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(metadata.Topics, t => t.Topic == scenario.Topic + ".dlt");
    }

    [Fact]
    public async Task Committed_batches_are_not_read_again_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(postgres, kafka);
        var first = new PingRecorder();
        using (var host = await StartConsumerAsync(scenario, first))
        {
            await ProduceAsync(host, scenario.Topic, Enumerable.Range(1, 10));
            await Scenario.EventuallyAsync(() => Task.FromResult(first.Handled.Count == 10), s_wait, "ten pings are handled");
            await host.StopAsync(ct);
        }

        var second = new PingRecorder();
        using var restarted = await StartConsumerAsync(scenario, second);
        await AssertOnlyMarkersAreReadAsync(scenario, second);
    }

    [Fact]
    public async Task Throwing_batch_is_retried_then_committed()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await CreateScenarioAsync(postgres, kafka);

        // Five pings of one shipment, in the topic before the consumer starts: the first batch holds all five.
        await SendAsync(scenario, Enumerable.Range(1, 5).Select(n => PingMessage(n, key: "one-shipment")));
        var recorder = new PingRecorder(failingCalls: 2);
        var logs = new LogCapture();
        using (var host = await StartConsumerAsync(
            scenario,
            recorder,
            o =>
            {
                o.StreamBatchWindow = TimeSpan.FromSeconds(1);
                o.TransientRetryPause = TimeSpan.FromMilliseconds(50);
                o.MaxTransientRetryPause = TimeSpan.FromMilliseconds(200);
            },
            logs))
        {
            await Scenario.EventuallyAsync(() => Task.FromResult(recorder.Calls.Count >= 3), s_wait, "the handler is called a third time");
            await host.StopAsync(ct);
        }

        var calls = recorder.Calls;
        Assert.Equal(3, calls.Count);
        Assert.Equal(Enumerable.Range(1, 5), calls[0].Select(p => p.N));
        Assert.Equal(calls[0], calls[1]);
        Assert.Equal(calls[0], calls[2]);
        var failures = logs.Snapshot().Where(l => l.Level == LogLevel.Warning && l.Message.Contains(scenario.Topic, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, failures.Count);
        Assert.Contains("attempt 1", failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("attempt 2", failures[1].Message, StringComparison.Ordinal);

        // The third call committed the batch: a new host in the same group reads only what comes after it.
        var second = new PingRecorder();
        using var restarted = await StartConsumerAsync(scenario, second);
        await AssertOnlyMarkersAreReadAsync(scenario, second);
    }

    [Fact]
    public async Task Undeserializable_message_is_skipped()
    {
        var scenario = await CreateScenarioAsync(postgres, kafka);
        var recorder = new PingRecorder();
        var logs = new LogCapture();
        using var host = await StartConsumerAsync(scenario, recorder, logs: logs);

        // One key, so one partition and offsets 0, 1, 2. "null" is valid JSON that deserialises to no Ping.
        var type = typeof(Ping).FullName!;
        await SendAsync(scenario, [RawMessage("not json", type), RawMessage("null", type), PingMessage(1, key: "k")]);
        await Scenario.EventuallyAsync(() => Task.FromResult(recorder.Handled.Count == 1), s_wait, "the good ping is handled");

        Assert.Equal([new Ping(1)], recorder.Handled);
        var warnings = logs.Snapshot()
            .Where(l => l.Level == LogLevel.Warning && l.Message.Contains(scenario.Topic + "[", StringComparison.Ordinal))
            .Select(l => l.Message)
            .ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("]@0", warnings[0], StringComparison.Ordinal);
        Assert.Contains("]@1", warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Message_of_another_type_is_skipped()
    {
        var scenario = await CreateScenarioAsync(postgres, kafka);
        var recorder = new PingRecorder();
        var logs = new LogCapture();
        using var host = await StartConsumerAsync(scenario, recorder, logs: logs);

        // A Ping's JSON under another type name, then a message with no type header, then a real Ping.
        await SendAsync(scenario, [RawMessage("""{"N":1}""", "NewStore.Messaging.Tests.Pong"), RawMessage("""{"N":2}""", type: null), PingMessage(3, key: "k")]);
        await Scenario.EventuallyAsync(() => Task.FromResult(recorder.Handled.Count == 1), s_wait, "the ping is handled");

        Assert.Equal([new Ping(3)], recorder.Handled);
        Assert.DoesNotContain(logs.Snapshot(), l => l.Level >= LogLevel.Warning && l.Message.Contains(scenario.Topic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_hosts_in_one_group_share_the_partitions()
    {
        var scenario = await CreateScenarioAsync(postgres, kafka);
        var a = new PingRecorder();
        var b = new PingRecorder();
        using var hostA = await StartConsumerAsync(scenario, a);
        using var hostB = await StartConsumerAsync(scenario, b);

        // Wait until the group is stable with both members holding partitions, so neither takes all six alone.
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = scenario.BootstrapServers }).Build();
        var group = $"{scenario.ClientId}.{scenario.Topic}";
        await Scenario.EventuallyAsync(
            async () =>
            {
                try
                {
                    var description = (await admin.DescribeConsumerGroupsAsync([group])).ConsumerGroupDescriptions.Single();
                    return description.State == ConsumerGroupState.Stable
                        && description.Members.Count == 2
                        && description.Members.All(m => m.Assignment.TopicPartitions.Count > 0);
                }
                catch (DescribeConsumerGroupsException ex)
                {
                    // The group does not exist until the first member has joined.
                    TestContext.Current.TestOutputHelper?.WriteLine($"describe failed: {ex.Results}");
                    return false;
                }
            },
            s_wait,
            "both hosts hold partitions of the stream");

        await ProduceAsync(hostA, scenario.Topic, Enumerable.Range(1, 60));
        await Scenario.EventuallyAsync(
            () => Task.FromResult(a.Handled.Concat(b.Handled).Select(p => p.N).Distinct().Count() == 60), s_wait, "60 pings are handled");

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"host A batch sizes: {string.Join(", ", a.Batches.Select(x => x.Count))}");
        output?.WriteLine($"host B batch sizes: {string.Join(", ", b.Batches.Select(x => x.Count))}");
        Assert.NotEmpty(a.Handled);
        Assert.NotEmpty(b.Handled);
        Assert.Equal(Enumerable.Range(1, 60), a.Handled.Concat(b.Handled).Select(p => p.N).Distinct().Order());
    }

    internal static async Task<Scenario> CreateScenarioAsync(PostgresServer postgres, KafkaServer kafka)
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka, createTopic: false);
        await kafka.CreateTopicAsync(scenario.Topic, Partitions);
        return scenario;
    }

    private static Task<IHost> StartConsumerAsync(
        Scenario scenario, PingRecorder recorder, Action<MessagingOptions>? options = null, LogCapture? logs = null) =>
        scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddStreamHandler<Ping, RecordingPingHandler>(scenario.Topic),
            options,
            s =>
            {
                s.AddSingleton(recorder);
                if (logs is not null)
                {
                    s.AddLogging(l => l.AddProvider(logs));
                }
            });

    /// <summary>Produces through the host's <see cref="IStreamProducer"/>, one key per ping, so they spread over the partitions.</summary>
    private static async Task ProduceAsync(IHost host, string topic, IEnumerable<int> numbers)
    {
        var producer = host.Services.GetRequiredService<IStreamProducer>();
        await Task.WhenAll(numbers.Select(n =>
            producer.ProduceAsync(topic, $"shipment-{n}", new Ping(n), TestContext.Current.CancellationToken)));
    }

    /// <summary>
    /// Sends one marker ping to each partition and waits until all six are handled. A partition is read in order, so a
    /// message that was not committed before the marker on its partition would be handled before it: the recorder then
    /// holds more than the markers.
    /// </summary>
    private static async Task AssertOnlyMarkersAreReadAsync(Scenario scenario, PingRecorder recorder)
    {
        var markers = Enumerable.Range(1000, Partitions).ToList();
        using (var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = scenario.BootstrapServers }).Build())
        {
            for (var partition = 0; partition < Partitions; partition++)
            {
                await producer.ProduceAsync(
                    new TopicPartition(scenario.Topic, partition), PingMessage(markers[partition]), TestContext.Current.CancellationToken);
            }
        }

        await Scenario.EventuallyAsync(
            () => Task.FromResult(markers.All(m => recorder.Handled.Any(p => p.N == m))), s_wait, "a marker on every partition is handled");
        Assert.Equal(markers, recorder.Handled.Select(p => p.N).Order());
    }

    private static Message<string, string> PingMessage(int n, string? key = null) =>
        RawMessage(JsonSerializer.Serialize(new Ping(n)), typeof(Ping).FullName, key ?? $"shipment-{n}");

    private static Message<string, string> RawMessage(string value, string? type, string key = "k")
    {
        var message = new Message<string, string> { Key = key, Value = value, Headers = [] };
        if (type is not null)
        {
            message.Headers.Add(new Header("type", Encoding.UTF8.GetBytes(type)));
        }

        return message;
    }

    /// <summary>Sends with a plain producer, in order, before or beside the host under test.</summary>
    private static async Task SendAsync(Scenario scenario, IEnumerable<Message<string, string>> messages)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = scenario.BootstrapServers }).Build();
        foreach (var message in messages)
        {
            await producer.ProduceAsync(scenario.Topic, message, TestContext.Current.CancellationToken);
        }
    }

    private static long[] EndOffsets(Scenario scenario)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = scenario.BootstrapServers,
            GroupId = $"test-offsets-{Guid.NewGuid():N}",
        }).Build();
        return [.. Enumerable.Range(0, Partitions)
            .Select(p => consumer.QueryWatermarkOffsets(new TopicPartition(scenario.Topic, p), TimeSpan.FromSeconds(10)).High.Value)];
    }
}

/// <summary>Pauses the broker, so it gets a Kafka of its own (<c>IClassFixture</c>) and cannot disturb the other tests.</summary>
public class StreamProducerOutageTests(PostgresServer postgres, StreamProducerOutageTests.OwnKafkaServer kafka)
    : IClassFixture<StreamProducerOutageTests.OwnKafkaServer>
{
    /// <summary>A second container, so the assembly's shared <see cref="KafkaServer"/> is not the one that gets paused.</summary>
    public sealed class OwnKafkaServer : KafkaServer;

    [Fact]
    public async Task Stream_producer_throws_when_kafka_does_not_ack()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await StreamTests.CreateScenarioAsync(postgres, kafka);
        using var host = await scenario.StartHostAsync(options: o => o.ProduceTimeout = TimeSpan.FromSeconds(1));
        var producer = host.Services.GetRequiredService<IStreamProducer>();

        // Warm-up: the producer is connected before the broker freezes, so the failure below is the missing ack. A fresh
        // broker may take longer than the 1 s timeout to answer the first message, so try until one is acknowledged.
        await Scenario.EventuallyAsync(
            async () =>
            {
                try
                {
                    await producer.ProduceAsync(scenario.Topic, "warm-up", new Ping(0), ct);
                    return true;
                }
                catch (ProduceException<string, string>)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(30),
            "a warm-up ping is acknowledged");

        await kafka.PauseAsync(ct);
        try
        {
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<ProduceException<string, string>>(
                () => producer.ProduceAsync(scenario.Topic, "frozen", new Ping(1), ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
            TestContext.Current.TestOutputHelper?.WriteLine($"ProduceAsync threw after {clock.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await kafka.UnpauseAsync(ct);
        }
    }
}
