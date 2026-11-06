using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

using NewStore.Catalog.Caching;
using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Channels;

public static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/channels/{slug}", async (
            CatalogDbContext db, HybridCache cache, string slug, CancellationToken ct,
            int page = 1, int pageSize = Paging.DefaultPageSize) =>
        {
            if (Paging.Validate(page, pageSize) is { } errors)
            {
                return Results.ValidationProblem(errors);
            }

            var key = $"channel:{slug}:{page}:{pageSize}";
            var channelPage = await cache.GetOrCreateAsync(
                key, (db, slug, page, pageSize, caller: Activity.Current),
                static (state, token) =>
                {
                    CacheTrace.Restore(state.caller);
                    return new ValueTask<ChannelPage?>(LoadAsync(state.db, state.slug, state.page, state.pageSize, token));
                },
                tags: [$"channel:{slug}"], cancellationToken: ct);
            if (channelPage is null)
            {
                // GetOrCreateAsync caches the null as well; a missing channel must be found once it exists.
                await cache.RemoveAsync(key, ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Channel not found.");
            }

            return Results.Ok(channelPage);
        });

        return app;
    }

    private static async Task<ChannelPage?> LoadAsync(CatalogDbContext db, string slug, int page, int pageSize, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Slug == slug, ct);
        if (channel is null)
        {
            return null;
        }

        var videos = await VideoQueries.OfChannelAsync(db, channel.Id, channel.Name, channel.Slug, page, pageSize, ct);
        return new ChannelPage(channel.Id, channel.Name, channel.Slug, channel.Description, videos);
    }
}
