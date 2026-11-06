using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NewStore.Messaging.Outbox;

namespace NewStore.Messaging.Tests;

public class OutboxRelayTests(PostgresServer postgres, KafkaServer kafka)
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Enqueued_event_is_published_after_commit()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        using var host = await scenario.StartHostAsync();
        var created = ThingCreated.New();

        // The relay copies the request's trace context into the message, so the test needs a current activity.
        using var request = new Activity("request");
        request.Start();
        await Scenario.CreateThingAsync(host, created, scenario.Topic);

        var messages = await scenario.ReadAsync(scenario.Topic, 1, s_wait);

        var message = Assert.Single(messages);
        Assert.Equal(created.ThingId.ToString(), message.Message.Key);
        Assert.Equal(created.EventId.ToString(), Scenario.Header(message, "message-id"));
        Assert.Equal(typeof(ThingCreated).FullName, Scenario.Header(message, "type"));
        Assert.Contains(request.TraceId.ToHexString(), Scenario.Header(message, "traceparent"), StringComparison.Ordinal);

        await using var context = scenario.OpenContext();
        await Scenario.EventuallyAsync(
            async () => await context.Set<OutboxMessage>().AsNoTracking()
                .AnyAsync(m => m.Id == created.EventId && m.PublishedAt != null, TestContext.Current.CancellationToken),
            s_wait,
            "published_at is set");
    }

    [Fact]
    public async Task Rolled_back_transaction_publishes_nothing()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        using var host = await scenario.StartHostAsync();
        var ct = TestContext.Current.CancellationToken;

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ThingsDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            var created = ThingCreated.New();
            context.Things.Add(new Thing { Id = created.ThingId, Name = created.Name });
            outbox.Enqueue(created, scenario.Topic, created.ThingId.ToString());
            await context.SaveChangesAsync(ct);
            // Leaving the block without CommitAsync rolls the transaction back.
        }

        var messages = await scenario.ReadAsync(scenario.Topic, 1, TimeSpan.FromSeconds(3));

        Assert.Empty(messages);
        await using var check = scenario.OpenContext();
        Assert.Equal(0, await check.Set<OutboxMessage>().CountAsync(ct));
        Assert.Equal(0, await check.Things.CountAsync(ct));
    }

    [Fact]
    public async Task Two_relays_publish_each_message_once()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        // A small batch makes the relays collide on the table: 200 rows are 20 batches.
        using var first = await scenario.StartHostAsync("relay-a", options: o => o.RelayBatchSize = 10);
        using var second = await scenario.StartHostAsync("relay-b", options: o => o.RelayBatchSize = 10);

        for (var i = 0; i < 10; i++)
        {
            using var scope = first.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ThingsDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            for (var j = 0; j < 20; j++)
            {
                var created = ThingCreated.New();
                outbox.Enqueue(created, scenario.Topic, created.ThingId.ToString());
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Ask for more than 200: a duplicate would show up as an extra message.
        var messages = await scenario.ReadAsync(scenario.Topic, 210, TimeSpan.FromSeconds(20));

        Assert.Equal(200, messages.Count);
        Assert.Equal(200, messages.Select(m => Scenario.Header(m, "message-id")).Distinct().Count());
    }

    [Fact]
    public async Task Relay_check_is_unhealthy_when_the_loop_stalls()
    {
        var time = new FakeTimeProvider();
        var heartbeat = new RelayHeartbeat();
        var check = new OutboxRelayHealthCheck(
            heartbeat, Options.Create(new MessagingOptions { BootstrapServers = "unused" }), time);
        var ct = TestContext.Current.CancellationToken;

        heartbeat.Stamp(time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), ct)).Status);

        // The limit is 3 x 1 s interval + 5 s produce timeout = 8 s.
        time.Advance(TimeSpan.FromSeconds(23));
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new HealthCheckContext(), ct)).Status);
    }
}

/// <summary>
/// Pauses the broker, so it gets a Kafka of its own (<c>IClassFixture</c>) and cannot disturb the other tests.
/// </summary>
public class OutboxRelayOutageTests(PostgresServer postgres, OutboxRelayOutageTests.OwnKafkaServer kafka)
    : IClassFixture<OutboxRelayOutageTests.OwnKafkaServer>
{
    /// <summary>A second container, so the assembly's shared <see cref="KafkaServer"/> is not the one that gets paused.</summary>
    public sealed class OwnKafkaServer : KafkaServer;

    [Fact]
    public async Task Relay_survives_a_kafka_outage_and_catches_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var produceTimeout = TimeSpan.FromSeconds(3);
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        using var host = await scenario.StartHostAsync(options: o => o.ProduceTimeout = produceTimeout);

        // Warm-up: the producer has connected before the broker freezes.
        var warmUp = ThingCreated.New("warm-up");
        await Scenario.CreateThingAsync(host, warmUp, scenario.Topic);
        await using var context = scenario.OpenContext();
        await Scenario.EventuallyAsync(
            async () => await context.Set<OutboxMessage>().AsNoTracking().AllAsync(m => m.PublishedAt != null, ct),
            TimeSpan.FromSeconds(30),
            "the warm-up message is published");

        await kafka.PauseAsync(ct);
        var created = ThingCreated.New("during-outage");
        await Scenario.CreateThingAsync(host, created, scenario.Topic);
        await Task.Delay(2 * produceTimeout, ct);

        var row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == created.EventId, ct);
        Assert.True(row.Attempts >= 1, $"attempts was {row.Attempts}");
        Assert.Null(row.PublishedAt);

        // The loop keeps running although every send fails, so the live check stays Healthy.
        var health = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == "outbox-relay", ct);
        Assert.Equal(HealthStatus.Healthy, health.Status);

        await kafka.UnpauseAsync(ct);

        var messages = await scenario.ReadAsync(scenario.Topic, 2, TimeSpan.FromSeconds(30));
        Assert.Contains(messages, m => Scenario.Header(m, "message-id") == created.EventId.ToString());
    }
}
