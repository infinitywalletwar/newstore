using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NewStore.Messaging.Inbox;

namespace NewStore.Messaging.Tests;

public class ConsumerTests(PostgresServer postgres, KafkaServer kafka)
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Handler_runs_once_for_a_duplicate_message()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        using var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, CreateThingHandler>(scenario.Topic),
            services: s => s.AddSingleton(probe));
        var duplicated = ThingCreated.New("twice");
        var marker = ThingCreated.New("marker");

        // The same message twice, then a marker: once the marker is handled, the duplicate was consumed too.
        await scenario.ProduceAsync(scenario.Topic, duplicated, times: 2);
        await scenario.ProduceAsync(scenario.Topic, marker);
        await Scenario.EventuallyAsync(() => Task.FromResult(probe.Completed(marker.EventId)), s_wait, "the marker is handled");

        Assert.Equal(1, probe.Calls(duplicated.EventId));
        await using var context = scenario.OpenContext();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(1, await context.Set<InboxMessage>().CountAsync(m => m.MessageId == duplicated.EventId, ct));
        Assert.Equal(1, await context.Things.CountAsync(t => t.Id == duplicated.ThingId, ct));
    }

    [Fact]
    public async Task Handler_writes_commit_with_the_inbox_row()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        using var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, CreateThingHandler>(scenario.Topic),
            services: s => s.AddSingleton(probe));
        var created = ThingCreated.New("committed");

        await scenario.ProduceAsync(scenario.Topic, created);
        await using var context = scenario.OpenContext();
        var ct = TestContext.Current.CancellationToken;
        await Scenario.EventuallyAsync(
            async () => await context.Set<InboxMessage>().AnyAsync(m => m.MessageId == created.EventId, ct),
            s_wait,
            "the inbox row exists");

        // The handler never called SaveChanges: the dispatcher saved its row in the inbox transaction.
        Assert.True(await context.Things.AnyAsync(t => t.Id == created.ThingId, ct));
        var inbox = await context.Set<InboxMessage>().SingleAsync(m => m.MessageId == created.EventId, ct);
        Assert.Equal(scenario.ClientId, inbox.Consumer);
    }

    [Fact]
    public async Task Poisoned_message_goes_to_the_dead_letter_topic_after_three_attempts()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        using var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, PoisonedHandler>(scenario.Topic),
            services: s => s.AddSingleton(probe));
        var poison = ThingCreated.New("poison");
        var next = ThingCreated.New("fine");

        await scenario.ProduceAsync(scenario.Topic, poison);
        await scenario.ProduceAsync(scenario.Topic, next);
        await Scenario.EventuallyAsync(() => Task.FromResult(probe.Completed(next.EventId)), s_wait, "the message after the poison is handled");

        Assert.Equal(3, probe.Calls(poison.EventId));
        var deadLetters = await scenario.ReadAsync(scenario.Topic + ".dlt", 1, s_wait);
        var deadLetter = Assert.Single(deadLetters);
        Assert.Equal(poison.EventId.ToString(), Scenario.Header(deadLetter, "message-id"));
        Assert.Contains("This message can never be handled.", Scenario.Header(deadLetter, "error"), StringComparison.Ordinal);
        Assert.Equal(scenario.Topic, Scenario.Header(deadLetter, "original-topic"));
        Assert.Equal("0", Scenario.Header(deadLetter, "original-partition"));
        Assert.Equal("0", Scenario.Header(deadLetter, "original-offset"));

        // The failed attempts rolled back, the row they added and the inbox row with it.
        await using var context = scenario.OpenContext();
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await context.Things.AnyAsync(t => t.Id == poison.ThingId, ct));
        Assert.False(await context.Set<InboxMessage>().AnyAsync(m => m.MessageId == poison.EventId, ct));
        Assert.True(await context.Things.AnyAsync(t => t.Id == next.ThingId, ct));
    }

    [Fact]
    public async Task Consumer_finishes_the_message_in_flight_before_stopping()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, SlowHandler>(scenario.Topic),
            services: s => s.AddSingleton(probe));
        var created = ThingCreated.New("slow");
        await scenario.ProduceAsync(scenario.Topic, created);

        await probe.Started.Task.WaitAsync(s_wait, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.True(probe.Completed(created.EventId), "the handler was cut off");
        await using var context = scenario.OpenContext();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await context.Set<InboxMessage>().AnyAsync(m => m.MessageId == created.EventId, ct));

        // A new consumer in the same group starts after the committed offset: it gets the next message, not this one.
        var control = ThingCreated.New("control");
        await scenario.ProduceAsync(scenario.Topic, control);
        using var successor = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = scenario.BootstrapServers,
            GroupId = scenario.ClientId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        successor.Subscribe(scenario.Topic);
        ConsumeResult<string, string>? first = null;
        await Scenario.EventuallyAsync(
            () =>
            {
                first = successor.Consume(TimeSpan.FromMilliseconds(500));
                return Task.FromResult(first is not null);
            },
            s_wait,
            "the successor receives a message");

        Assert.Equal(control.EventId.ToString(), Scenario.Header(first!, "message-id"));
    }

    [Fact]
    public async Task Topics_are_created_once_and_a_second_start_does_not_fail()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka, createTopic: false);
        var topic = scenario.Topic;
        void Configure(MessagingBuilder b) => b
            .AddTopic(topic, partitions: 3)
            .AddHandler<ThingCreated, CreateThingHandler>(topic);
        void Register(IServiceCollection s) => s.AddSingleton(new HandlerProbe());

        using (var first = await scenario.StartHostAsync(scenario.ClientId, Configure, services: Register))
        {
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        using var second = await scenario.StartHostAsync(scenario.ClientId, Configure, services: Register);

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = scenario.BootstrapServers }).Build();
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        Assert.Equal(3, metadata.Topics.Single(t => t.Topic == topic).Partitions.Count);
        Assert.Single(metadata.Topics.Single(t => t.Topic == topic + ".dlt").Partitions);
    }
}
