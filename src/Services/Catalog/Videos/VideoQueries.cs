using Microsoft.EntityFrameworkCore;

namespace NewStore.Catalog.Videos;

/// <summary>The database reads behind the video and channel endpoints.</summary>
public static class VideoQueries
{
    /// <summary>
    /// A page of videos, newest first. With <paramref name="search"/>: videos whose text matches the web-style
    /// query or whose channel name contains it, best text rank first, then newest.
    /// </summary>
    public static async Task<PagedResult<VideoSummary>> SearchAsync(
        CatalogDbContext db, string? search, int page, int pageSize, CancellationToken ct)
    {
        var rows = from v in db.Videos
                   join c in db.Channels on v.ChannelId equals c.Id
                   select new { Video = v, Channel = c };

        if (!string.IsNullOrWhiteSpace(search))
        {
            // The tsquery is built inside each lambda: EF translates the call, it can't run it in memory.
            var pattern = $"%{EscapeLike(search)}%";
            rows = rows.Where(r => r.Video.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", search)) || EF.Functions.ILike(r.Channel.Name, pattern));
            return await PageOfAsync(
                rows.OrderByDescending(r => r.Video.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", search)))
                    .ThenByDescending(r => r.Video.PublishedAt).ThenBy(r => r.Video.Id),
                r => new VideoSummary(r.Video.Id, r.Video.Title, r.Video.DurationSeconds, r.Video.PublishedAt,
                    new ChannelRef(r.Channel.Id, r.Channel.Name, r.Channel.Slug)),
                page, pageSize, ct);
        }

        return await PageOfAsync(
            rows.OrderByDescending(r => r.Video.PublishedAt).ThenBy(r => r.Video.Id),
            r => new VideoSummary(r.Video.Id, r.Video.Title, r.Video.DurationSeconds, r.Video.PublishedAt,
                new ChannelRef(r.Channel.Id, r.Channel.Name, r.Channel.Slug)),
            page, pageSize, ct);
    }

    /// <summary>A channel's videos, newest first.</summary>
    public static Task<PagedResult<VideoSummary>> OfChannelAsync(
        CatalogDbContext db, Guid channelId, string name, string slug, int page, int pageSize, CancellationToken ct)
    {
        var channel = new ChannelRef(channelId, name, slug);
        return PageOfAsync(
            db.Videos.Where(v => v.ChannelId == channelId).OrderByDescending(v => v.PublishedAt).ThenBy(v => v.Id),
            v => new VideoSummary(v.Id, v.Title, v.DurationSeconds, v.PublishedAt, channel),
            page, pageSize, ct);
    }

    public static async Task<VideoDetails?> FindAsync(CatalogDbContext db, Guid id, CancellationToken ct) =>
        await (from v in db.Videos
               join c in db.Channels on v.ChannelId equals c.Id
               where v.Id == id
               select new VideoDetails(v.Id, v.Title, v.Description, v.DurationSeconds, v.PublishedAt,
                   new ChannelRef(c.Id, c.Name, c.Slug)))
            .FirstOrDefaultAsync(ct);

    private static async Task<PagedResult<TOut>> PageOfAsync<TIn, TOut>(
        IQueryable<TIn> ordered, System.Linq.Expressions.Expression<Func<TIn, TOut>> select,
        int page, int pageSize, CancellationToken ct)
    {
        var total = await ordered.CountAsync(ct);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(select).ToListAsync(ct);
        return new PagedResult<TOut>(items, page, pageSize, total);
    }

    // ILIKE treats \, % and _ as pattern characters; escape them so "50%" searches for the text "50%".
    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
