using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NewStore.Messaging.Inbox;

namespace NewStore.Messaging.Tests;

/// <summary>A second event on the things topic, which the test service does not handle.</summary>
public sealed record ThingArchived(Guid EventId, DateTimeOffset OccurredAt, Guid ThingId) : IIntegrationEvent
{
    public static ThingArchived New() => new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7());
}

public class IgnoredTypeTests(PostgresServer postgres, KafkaServer kafka)
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Ignored_type_is_skipped_without_a_warning_or_an_inbox_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var logs = new LogCapture();
        var probe = new HandlerProbe();
        using var host = await StartAsync(scenario, probe, logs, b => b.AddIgnoredType<ThingArchived>(scenario.Topic));
        var archived = ThingArchived.New();

        await SendArchivedThenMarkerAsync(scenario, probe, archived);

        Assert.DoesNotContain(logs.Snapshot(), l => l.Level >= LogLevel.Warning && l.Message.Contains(nameof(ThingArchived), StringComparison.Ordinal));
        await using var context = scenario.OpenContext();
        Assert.False(await context.Set<InboxMessage>().AnyAsync(m => m.MessageId == archived.EventId, ct));
    }

    [Fact]
    public async Task Unknown_type_still_warns()
    {
        var scenario = await Scenario.CreateAsync(postgres, kafka);
        var logs = new LogCapture();
        var probe = new HandlerProbe();
        using var host = await StartAsync(scenario, probe, logs, _ => { });

        await SendArchivedThenMarkerAsync(scenario, probe, ThingArchived.New());

        Assert.Contains(logs.Snapshot(), l => l.Level == LogLevel.Warning && l.Message.Contains(typeof(ThingArchived).FullName!, StringComparison.Ordinal));
    }

    private static Task<IHost> StartAsync(Scenario scenario, HandlerProbe probe, LogCapture logs, Action<MessagingBuilder> ignore) =>
        scenario.StartHostAsync(
            scenario.ClientId,
            b =>
            {
                b.AddHandler<ThingCreated, CreateThingHandler>(scenario.Topic);
                ignore(b);
            },
            services: s => s.AddSingleton(probe).AddLogging(l => l.AddProvider(logs)));

    /// <summary>One partition: once the marker after it is handled, the archived event has been read too.</summary>
    private async Task SendArchivedThenMarkerAsync(Scenario scenario, HandlerProbe probe, ThingArchived archived)
    {
        var marker = ThingCreated.New("marker");
        await kafka.PublishAsync(scenario.Topic, archived.ThingId.ToString(), archived, TestContext.Current.CancellationToken);
        await scenario.ProduceAsync(scenario.Topic, marker);
        await Scenario.EventuallyAsync(() => Task.FromResult(probe.Completed(marker.EventId)), s_wait, "the marker is handled");
    }
}
