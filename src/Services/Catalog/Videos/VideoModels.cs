namespace NewStore.Catalog.Videos;

public sealed record ChannelRef(Guid Id, string Name, string Slug);

public sealed record VideoSummary(
    Guid Id, string Title, int DurationSeconds, DateTimeOffset PublishedAt, ChannelRef Channel, string? ThumbnailUrl);

public sealed record VideoDetails(
    Guid Id, string Title, string Description, int DurationSeconds, DateTimeOffset PublishedAt, ChannelRef Channel,
    string? ThumbnailUrl, string? PlaybackUrl);

public sealed record CreateVideoRequest(string Title, string? Description);

/// <summary>A video as its creator sees it in <c>/me/videos</c>: every status.</summary>
public sealed record MyVideo(
    Guid Id, string Title, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, string? ThumbnailUrl);

/// <summary>
/// Who owns a video: the creator of its channel, and the video's status. Inventory asks before it lets a creator sell
/// the video (it ignores the status); Media asks before it lets a creator upload.
/// </summary>
public sealed record VideoOwner(Guid VideoId, Guid CreatorId, string Status);
