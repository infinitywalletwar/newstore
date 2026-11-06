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
    }
}
