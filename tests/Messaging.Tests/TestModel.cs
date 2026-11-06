using Microsoft.EntityFrameworkCore;

namespace NewStore.Messaging.Tests;

public sealed class Thing
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }
}

public sealed record ThingCreated(Guid EventId, DateTimeOffset OccurredAt, Guid ThingId, string Name) : IIntegrationEvent
{
    public static ThingCreated New(string name = "thing") =>
        new(Guid.CreateVersion7(), DateTimeOffset.UtcNow, Guid.CreateVersion7(), name);
}

public sealed class ThingsDbContext(DbContextOptions<ThingsDbContext> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Thing>().HasKey(t => t.Id);
        modelBuilder.AddOutbox();
        modelBuilder.AddInbox();
    }
}

/// <summary>What the test handlers record, registered as a singleton so the test can read it.</summary>
public sealed class HandlerProbe
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _calls = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> _completed = new();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls(Guid eventId) => _calls.GetValueOrDefault(eventId);

    public bool Completed(Guid eventId) => _completed.ContainsKey(eventId);

    internal int RecordCall(Guid eventId) => _calls.AddOrUpdate(eventId, 1, (_, n) => n + 1);

    internal void RecordCompleted(Guid eventId) => _completed[eventId] = true;
}

/// <summary>Adds a <see cref="Thing"/> through the consumer's context, so the test can see it commit with the inbox row.</summary>
public sealed class CreateThingHandler(ThingsDbContext context, HandlerProbe probe) : IIntegrationEventHandler<ThingCreated>
{
    public Task HandleAsync(ThingCreated integrationEvent, CancellationToken cancellationToken)
    {
        probe.RecordCall(integrationEvent.EventId);
        context.Things.Add(new Thing { Id = integrationEvent.ThingId, Name = integrationEvent.Name });
        probe.RecordCompleted(integrationEvent.EventId);
        return Task.CompletedTask;
    }
}

/// <summary>Writes a row and then throws for a thing named "poison", to show that the row does not survive.</summary>
public sealed class PoisonedHandler(ThingsDbContext context, HandlerProbe probe) : IIntegrationEventHandler<ThingCreated>
{
    public Task HandleAsync(ThingCreated integrationEvent, CancellationToken cancellationToken)
    {
        probe.RecordCall(integrationEvent.EventId);
        context.Things.Add(new Thing { Id = integrationEvent.ThingId, Name = integrationEvent.Name });
        if (integrationEvent.Name == "poison")
        {
            throw new InvalidOperationException("This message can never be handled.");
        }

        probe.RecordCompleted(integrationEvent.EventId);
        return Task.CompletedTask;
    }
}

/// <summary>Takes two seconds, ignoring the cancellation token the way a handler in flight must be allowed to finish.</summary>
public sealed class SlowHandler(ThingsDbContext context, HandlerProbe probe) : IIntegrationEventHandler<ThingCreated>
{
    public async Task HandleAsync(ThingCreated integrationEvent, CancellationToken cancellationToken)
    {
        probe.RecordCall(integrationEvent.EventId);
        probe.Started.TrySetResult();
        await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        context.Things.Add(new Thing { Id = integrationEvent.ThingId, Name = integrationEvent.Name });
        probe.RecordCompleted(integrationEvent.EventId);
    }
}
