using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NewStore.Messaging.Cleanup;
using NewStore.Messaging.Inbox;
using NewStore.Messaging.Outbox;

namespace NewStore.Messaging.Tests;

public class CleanupTests(PostgresServer postgres, KafkaServer kafka)
{
    private static readonly MessagingOptions s_defaults = new() { BootstrapServers = "unused" };

    [Fact]
    public async Task Old_published_outbox_rows_and_old_inbox_rows_are_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await Scenario.CreateAsync(postgres, kafka, createTopic: false);
        var now = DateTimeOffset.UtcNow;
        var publishedLongAgo = OutboxRow(now.AddDays(-2), publishedAt: now.AddDays(-2));
        var publishedRecently = OutboxRow(now.AddHours(-1), publishedAt: now.AddHours(-1));
        // Never sent, however old: it is still waiting for Kafka.
        var unpublishedOld = OutboxRow(now.AddDays(-30), publishedAt: null);
        var unpublishedNew = OutboxRow(now, publishedAt: null);
        var handledLongAgo = new InboxMessage { Consumer = "things", MessageId = Guid.CreateVersion7(), HandledAt = now.AddDays(-8) };
        var handledRecently = new InboxMessage { Consumer = "things", MessageId = Guid.CreateVersion7(), HandledAt = now.AddHours(-1) };

        await using (var seed = scenario.OpenContext())
        {
            seed.AddRange(publishedLongAgo, publishedRecently, unpublishedOld, unpublishedNew, handledLongAgo, handledRecently);
            await seed.SaveChangesAsync(ct);
        }

        await using var context = scenario.OpenContext();
        var result = await MessagingCleanup.CleanAsync(context, s_defaults, now, ct);

        Assert.Equal(new CleanupResult(1, 1), result);
        var outbox = await context.Set<OutboxMessage>().AsNoTracking().Select(m => m.Id).ToListAsync(ct);
        Assert.Equal(
            new[] { publishedRecently.Id, unpublishedOld.Id, unpublishedNew.Id }.Order(),
            outbox.Order());
        var inbox = await context.Set<InboxMessage>().AsNoTracking().Select(m => m.MessageId).ToListAsync(ct);
        Assert.Equal(handledRecently.MessageId, Assert.Single(inbox));
    }

    [Fact]
    public async Task Nothing_to_delete_returns_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await Scenario.CreateAsync(postgres, kafka, createTopic: false);
        await using var context = scenario.OpenContext();

        var result = await MessagingCleanup.CleanAsync(context, s_defaults, DateTimeOffset.UtcNow, ct);

        Assert.Equal(new CleanupResult(0, 0), result);
    }

    [Fact]
    public async Task Cleanup_runs_on_the_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = await Scenario.CreateAsync(postgres, kafka, createTopic: false);
        var time = new FakeTimeProvider();
        var old = OutboxRow(time.GetUtcNow().AddDays(-2), publishedAt: time.GetUtcNow().AddDays(-2));
        await using (var seed = scenario.OpenContext())
        {
            seed.Add(old);
            await seed.SaveChangesAsync(ct);
        }

        // Only the cleanup service runs: no Kafka, no relay, and the fake clock is the only clock it sees.
        var services = new ServiceCollection();
        services.AddScoped(_ => scenario.OpenContext());
        await using var provider = services.BuildServiceProvider();
        using var cleanup = new MessagingCleanupService<ThingsDbContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(s_defaults),
            time,
            NullLogger<MessagingCleanupService<ThingsDbContext>>.Instance);

        // StartAsync runs ExecuteAsync up to its first await, so the timer exists when it returns.
        await cleanup.StartAsync(ct);
        try
        {
            // Not at startup: the row survives until the first interval has passed.
            await using var probe = scenario.OpenContext();
            Assert.True(await probe.Set<OutboxMessage>().AnyAsync(ct));

            time.Advance(s_defaults.CleanupInterval);

            await Scenario.EventuallyAsync(
                async () => !await probe.Set<OutboxMessage>().AsNoTracking().AnyAsync(ct),
                TimeSpan.FromSeconds(30),
                "the old published row is deleted after one interval");
        }
        finally
        {
            await cleanup.StopAsync(CancellationToken.None);
        }
    }

    private static OutboxMessage OutboxRow(DateTimeOffset occurredAt, DateTimeOffset? publishedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        Topic = "things",
        Key = "k",
        Type = "t",
        Payload = "{}",
        OccurredAt = occurredAt,
        PublishedAt = publishedAt,
    };
}
