namespace NewStore.Catalog.Channels;

/// <summary>A creator's channel. Its <see cref="Slug"/> is the public address: lower-case letters, digits and hyphens.</summary>
public sealed class Channel
{
    public const int NameMaxLength = 80;

    public Guid Id { get; init; }

    public Guid CreatorId { get; init; }

    public required string Name { get; init; }

    public required string Slug { get; init; }

    public required string Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
