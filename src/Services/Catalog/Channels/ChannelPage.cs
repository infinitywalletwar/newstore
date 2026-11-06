using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Channels;

public sealed record ChannelPage(Guid Id, string Name, string Slug, string Description, PagedResult<VideoSummary> Videos);
