using System.Diagnostics;
using System.Security.Claims;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

using NewStore.Catalog.Caching;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Catalog.Videos;

public static class VideoEndpoints
{
    public static IEndpointRouteBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/videos", async (
            CatalogDbContext db, IOptions<CatalogOptions> options, string? q, CancellationToken ct,
            int page = 1, int pageSize = Paging.DefaultPageSize) =>
            Paging.Validate(page, pageSize) is { } errors
                ? Results.ValidationProblem(errors)
                : Results.Ok(await VideoQueries.SearchAsync(db, options.Value.MediaBaseUrl, q, page, pageSize, ct)));

        app.MapGet("/videos/{id:guid}", async (
            CatalogDbContext db, HybridCache cache, IOptions<CatalogOptions> options, Guid id, CancellationToken ct) =>
        {
            var mediaBaseUrl = options.Value.MediaBaseUrl;
            var key = $"video:{id}";
            // The channel is unknown until the row is read, so the tag is the video's own: a writer that
            // changes a channel drops its videos' tags (ADR 0009).
            var video = await cache.GetOrCreateAsync(
                key, (db, mediaBaseUrl, id, caller: Activity.Current),
                static (state, token) =>
                {
                    CacheTrace.Restore(state.caller);
                    return new ValueTask<VideoDetails?>(VideoQueries.FindAsync(state.db, state.mediaBaseUrl, state.id, token));
                },
                tags: [$"video:{id}"], cancellationToken: ct);
            if (video is null)
            {
                // GetOrCreateAsync caches the null as well; a missing video (or a draft) must be found once it exists (or is published).
                await cache.RemoveAsync(key, ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Video not found.");
            }

            return Results.Ok(video);
        });

        // A draft is invisible here: /videos/{id} answers 404 for it, and the creator reads it in /me/videos.
        app.MapPost("/videos", CreateAsync).RequireAuthorization(Policies.SignedIn);
        app.MapGet("/me/videos", async (ClaimsPrincipal user, CatalogDbContext db, IOptions<CatalogOptions> options, CancellationToken ct) =>
            Results.Ok(await VideoQueries.OfCreatorAsync(db, options.Value.MediaBaseUrl, user.UserId(), ct)))
            .RequireAuthorization(Policies.SignedIn);

        // For Inventory and Media only. Read straight from the database: ownership decides who may sell or upload, so no cache.
        app.MapGet("/videos/{id:guid}/owner", async (CatalogDbContext db, Guid id, CancellationToken ct) =>
            await db.Videos.AsNoTracking()
                .Where(v => v.Id == id)
                .Join(db.Channels, v => v.ChannelId, c => c.Id, (v, c) => new { v.Id, c.CreatorId, v.Status })
                .FirstOrDefaultAsync(ct) is { } owner
                ? Results.Ok(new VideoOwner(owner.Id, owner.CreatorId, owner.Status.ToString()))
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Video not found."))
            .RequireAuthorization(Policies.Clients("inventory", "media"));

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateVideoRequest request, ClaimsPrincipal user, CatalogDbContext db, TimeProvider time,
        IOptions<CatalogOptions> options, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        // The creator is the token's subject; their channel is the one they own.
        var creatorId = user.UserId();
        var channelId = await db.Channels.Where(c => c.CreatorId == creatorId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (channelId is null)
        {
            return Problems.Conflict("no-channel", "Open a channel before you add a video.");
        }

        var video = new Video
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId.Value,
            Title = request.Title.Trim(),
            Description = request.Description ?? "",
            Status = VideoStatus.Draft,
            CreatedAt = time.GetUtcNow(),
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/videos/{video.Id}", new MyVideo(video.Id, video.Title, video.Status.ToString(), video.CreatedAt, null, null));
    }

    private static Dictionary<string, string[]> Validate(CreateVideoRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > Video.TitleMaxLength)
        {
            errors["title"] = [$"title must be 1 to {Video.TitleMaxLength} characters."];
        }

        if (request.Description?.Length > Video.DescriptionMaxLength)
        {
            errors["description"] = [$"description must be at most {Video.DescriptionMaxLength} characters."];
        }

        return errors;
    }
}
