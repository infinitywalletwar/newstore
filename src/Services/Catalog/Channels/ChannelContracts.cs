namespace NewStore.Catalog.Channels;

public sealed record CreateChannelRequest(string Name, string Slug, string? Description);

public sealed record ChannelResponse(Guid Id, string Name, string Slug, string? Description);
