using System.Diagnostics;

using Microsoft.Extensions.Caching.Hybrid;

using NewStore.Catalog.Caching;

namespace NewStore.Catalog.Videos;

public static class VideoEndpoints
{
    public static IEndpointRouteBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/videos", async (
            CatalogDbContext db, string? q, CancellationToken ct,
            int page = 1, int pageSize = Paging.DefaultPageSize) =>
            Paging.Validate(page, pageSize) is { } errors
                ? Results.ValidationProblem(errors)
                : Results.Ok(await VideoQueries.SearchAsync(db, q, page, pageSize, ct)));

        app.MapGet("/videos/{id:guid}", async (CatalogDbContext db, HybridCache cache, Guid id, CancellationToken ct) =>
        {
            var key = $"video:{id}";
            // The channel is unknown until the row is read, so the tag is the video's own: a writer that
            // changes a channel drops its videos' tags (ADR 0009).
            var video = await cache.GetOrCreateAsync(
                key, (db, id, caller: Activity.Current),
                static (state, token) =>
                {
                    CacheTrace.Restore(state.caller);
                    return new ValueTask<VideoDetails?>(VideoQueries.FindAsync(state.db, state.id, token));
                },
                tags: [$"video:{id}"], cancellationToken: ct);
            if (video is null)
            {
                // GetOrCreateAsync caches the null as well; a missing video must be found once it exists.
                await cache.RemoveAsync(key, ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Video not found.");
            }

            return Results.Ok(video);
        });

        return app;
    }
}
