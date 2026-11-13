namespace NewStore.Catalog.Videos;

public sealed record ChannelRef(Guid Id, string Name, string Slug);

public sealed record VideoSummary(Guid Id, string Title, int DurationSeconds, DateTimeOffset PublishedAt, ChannelRef Channel);

public sealed record VideoDetails(
    Guid Id, string Title, string Description, int DurationSeconds, DateTimeOffset PublishedAt, ChannelRef Channel);

/// <summary>Who owns a video: the creator of its channel. Inventory asks for it before it lets a creator sell the video.</summary>
public sealed record VideoOwner(Guid VideoId, Guid CreatorId);
