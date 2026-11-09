using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Messaging.Inbox;

namespace NewStore.Messaging.Tests;

public class TransientFailureTests(PostgresServer postgres, KafkaServer kafka)
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    private static void FastRetries(MessagingOptions o)
    {
        o.TransientRetryPause = TimeSpan.FromMilliseconds(50);
        o.MaxTransientRetryPause = TimeSpan.FromMilliseconds(200);
    }

    [Fact]
    public async Task Transient_failure_is_retried_until_it_succeeds_and_never_dead_lettered()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        var plan = new FlakyPlan(failuresPerMessage: 5);
        using var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, FlakyHandler>(scenario.Topic),
            FastRetries,
            s => s.AddSingleton(probe).AddSingleton(plan));
        var created = ThingCreated.New("flaky");

        await scenario.ProduceAsync(scenario.Topic, created);
        await Scenario.EventuallyAsync(() => Task.FromResult(probe.Completed(created.EventId)), s_wait, "the message is handled");

        Assert.Equal(6, probe.Calls(created.EventId));
        var ct = TestContext.Current.CancellationToken;
        await using var context = scenario.OpenContext();
        Assert.Equal(1, await context.Set<InboxMessage>().CountAsync(m => m.MessageId == created.EventId, ct));
        Assert.Equal(1, await context.Things.CountAsync(t => t.Id == created.ThingId, ct));
        Assert.Empty(await scenario.ReadAsync(scenario.Topic + ".dlt", 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Transient_failure_holds_back_later_messages_on_the_partition()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var probe = new HandlerProbe();
        var first = ThingCreated.New("A");
        var second = ThingCreated.New("B");
        var plan = new FlakyPlan(failuresPerMessage: 2);
        using var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, FlakyHandler>(scenario.Topic),
            FastRetries,
            s => s.AddSingleton(probe).AddSingleton(plan));

        // Same key, so the same partition. B would succeed at once: the plan fails only "A".
        plan.FailingNames.Add("A");
        await scenario.ProduceAsync(scenario.Topic, first, key: "same-key");
        await scenario.ProduceAsync(scenario.Topic, second, key: "same-key");
        await Scenario.EventuallyAsync(() => Task.FromResult(probe.Completed(second.EventId)), s_wait, "B is handled");

        Assert.Equal(["A", "A", "A", "B"], plan.Seen.ToArray());
    }

    [Fact]
    public async Task Stopping_during_transient_retries_leaves_the_message_for_the_next_start()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var stuckProbe = new HandlerProbe();
        var stuckPlan = new FlakyPlan(failuresPerMessage: int.MaxValue);
        using var host = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, FlakyHandler>(scenario.Topic),
            FastRetries,
            s => s.AddSingleton(stuckProbe).AddSingleton(stuckPlan));
        var created = ThingCreated.New("stuck");
        await scenario.ProduceAsync(scenario.Topic, created);

        await Scenario.EventuallyAsync(() => Task.FromResult(stuckProbe.Calls(created.EventId) >= 2), s_wait, "two attempts");
        await host.StopAsync(TestContext.Current.CancellationToken);

        var probe = new HandlerProbe();
        using var next = await scenario.StartHostAsync(
            scenario.ClientId,
            b => b.AddHandler<ThingCreated, CreateThingHandler>(scenario.Topic),
            FastRetries,
            s => s.AddSingleton(probe));
        await Scenario.EventuallyAsync(() => Task.FromResult(probe.Completed(created.EventId)), s_wait, "the new host handles it");

        Assert.Equal(1, probe.Calls(created.EventId));
        var ct = TestContext.Current.CancellationToken;
        await using var context = scenario.OpenContext();
        Assert.Equal(1, await context.Set<InboxMessage>().CountAsync(m => m.MessageId == created.EventId, ct));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Classifies_failures(Exception failure, bool expected) =>
        Assert.Equal(expected, TransientFailures.IsTransient(failure));

    public static TheoryData<Exception, bool> Failures => new()
    {
        { new TransientFailureException("down"), true },
        { new HttpRequestException("refused"), true },
        { new TimeoutException(), true },
        { new InvalidOperationException("wrapped", new HttpRequestException("refused")), true },
        { new InvalidOperationException("broken"), false },
        { new JsonException("bad json"), false },
        { new HttpRequestException("not found", null, HttpStatusCode.NotFound), false },
        { new HttpRequestException("conflict", null, HttpStatusCode.Conflict), false },
        { new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable), true },
        { new HttpRequestException("slow", null, HttpStatusCode.RequestTimeout), true },
        { new HttpRequestException("throttled", null, HttpStatusCode.TooManyRequests), true },
        { new TaskCanceledException("timed out", new TimeoutException()), true },
        { new FakeDbException(isTransient: true), true },
        { new FakeDbException(isTransient: false), false },
        { new AggregateException(new TimeoutException(), new HttpRequestException("refused")), true },
        { new AggregateException(new TimeoutException(), new InvalidOperationException("broken")), false },
    };

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(10, 30000)]
    [InlineData(1000, 30000)]
    public void Pause_doubles_up_to_the_cap(int attempt, int expectedMilliseconds)
    {
        var options = new MessagingOptions { BootstrapServers = "unused" };

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), options.TransientPauseFor(attempt));
    }

    private sealed class FakeDbException(bool isTransient) : DbException
    {
        public override bool IsTransient => isTransient;
    }
}

/// <summary>What <see cref="FlakyHandler"/> does: how often a message fails, and the order the handler saw names.</summary>
public sealed class FlakyPlan(int failuresPerMessage)
{
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    /// <summary>When not empty, only messages with these names fail.</summary>
    public ConcurrentBag<string> FailingNames { get; } = [];

    public ConcurrentQueue<string> Seen { get; } = new();

    internal bool ShouldFail(ThingCreated created)
    {
        Seen.Enqueue(created.Name);
        if (!FailingNames.IsEmpty && !FailingNames.Contains(created.Name))
        {
            return false;
        }

        return _failures.AddOrUpdate(created.EventId, 1, (_, n) => n + 1) <= failuresPerMessage;
    }
}

/// <summary>Throws <see cref="TransientFailureException"/> as the plan says, then writes its row like a normal handler.</summary>
public sealed class FlakyHandler(ThingsDbContext context, HandlerProbe probe, FlakyPlan plan) : IIntegrationEventHandler<ThingCreated>
{
    public Task HandleAsync(ThingCreated integrationEvent, CancellationToken cancellationToken)
    {
        probe.RecordCall(integrationEvent.EventId);
        if (plan.ShouldFail(integrationEvent))
        {
            throw new TransientFailureException("The dependency is down.");
        }

        context.Things.Add(new Thing { Id = integrationEvent.ThingId, Name = integrationEvent.Name });
        probe.RecordCompleted(integrationEvent.EventId);
        return Task.CompletedTask;
    }
}
