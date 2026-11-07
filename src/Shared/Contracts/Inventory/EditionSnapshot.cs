using NewStore.Messaging;

namespace NewStore.Contracts.Inventory;

/// <summary>
/// The public state of one edition, as Inventory last saw it (ADR 0011). Inventory publishes a snapshot when the
/// state changed since the previous one, so many reserves between two publishing passes make one snapshot. A consumer
/// keeps the snapshot with the highest <see cref="Version"/> and ignores an older one that arrives later.
/// </summary>
public sealed record EditionSnapshot : IIntegrationEvent
{
    /// <summary>The topic. The key is <see cref="EditionId"/>, so one edition's snapshots share a partition.</summary>
    public const string Topic = "inventory.editions";

    /// <summary>Both the producer and the consumer declare the topic with this count, so either may create it.</summary>
    public const int Partitions = 3;

    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid EditionId { get; init; }

    public required Guid VideoId { get; init; }

    /// <summary><c>Digital</c> or <c>Dvd</c>. A string, so the JSON does not depend on an enum's numbering.</summary>
    public required string Format { get; init; }

    public required decimal Price { get; init; }

    public required int CopiesTotal { get; init; }

    /// <summary>Copies nobody holds when the snapshot was taken. 0 after the sale ended.</summary>
    public required int CopiesLeft { get; init; }

    /// <summary>When the creator ended the sale, or null while it runs.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>1 for an edition's first snapshot, then one more for each later one.</summary>
    public required long Version { get; init; }
}
