namespace NewStore.Catalog.Videos;

public sealed record ChannelRef(Guid Id, string Name, string Slug);

public sealed record VideoSummary(Guid Id, string Title, int DurationSeconds, DateTimeOffset PublishedAt, ChannelRef Channel);

public sealed record VideoDetails(
    Guid Id, string Title, string Description, int DurationSeconds, DateTimeOffset PublishedAt, ChannelRef Channel);
