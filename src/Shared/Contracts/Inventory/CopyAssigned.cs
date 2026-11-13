using NewStore.Messaging;

namespace NewStore.Contracts.Inventory;

/// <summary>
/// A confirmed reservation got its copy number: the buyer now owns copy <see cref="CopyNumber"/> of the edition. Inventory
/// enqueues it in the transaction of the first confirm, so a repeated confirm publishes nothing.
/// </summary>
public sealed record CopyAssigned : IIntegrationEvent
{
    /// <summary>The topic. The key is <see cref="EditionId"/>, so one edition's copies are announced in the order they were assigned.</summary>
    public const string Topic = "inventory.copies";

    /// <summary>Both the producer and a consumer declare the topic with this count, so either may create it.</summary>
    public const int Partitions = 3;

    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid EditionId { get; init; }

    public required Guid VideoId { get; init; }

    /// <summary><c>Digital</c> or <c>Dvd</c>. A string, so the JSON does not depend on an enum's numbering.</summary>
    public required string Format { get; init; }

    public required Guid OwnerId { get; init; }

    public required int CopyNumber { get; init; }
}
