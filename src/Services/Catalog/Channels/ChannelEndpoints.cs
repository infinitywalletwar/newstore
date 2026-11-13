using System.Diagnostics;
using System.Security.Claims;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

using NewStore.Catalog.Caching;
using NewStore.Catalog.Videos;
using NewStore.ServiceDefaults.Security;

using Npgsql;

namespace NewStore.Catalog.Channels;

public static partial class ChannelEndpoints
{
    // \z, not $: in .NET "$" also matches before a final newline.
    [GeneratedRegex(@"^[a-z0-9-]{3,40}\z")]
    private static partial Regex SlugPattern();

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

        // Any signed-in user may open a channel; the creator is the token's subject, never a field of the request.
        app.MapPost("/channels", CreateAsync).RequireAuthorization(Policies.SignedIn);

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateChannelRequest request, ClaimsPrincipal user, CatalogDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        var channel = new Channel
        {
            Id = Guid.CreateVersion7(),
            CreatorId = user.UserId(),
            Name = request.Name.Trim(),
            Slug = request.Slug,
            Description = request.Description ?? "",
            CreatedAt = time.GetUtcNow(),
        };
        db.Channels.Add(channel);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation)
        {
            // The unique indexes decide, not a read before the insert: two concurrent requests cannot both pass.
            return violation.ConstraintName == CatalogDbContext.ChannelCreatorIndex
                ? Problems.Conflict("channel-exists", "You already have a channel.")
                : Problems.Conflict("slug-taken", "This slug is taken.");
        }

        return Results.Created(
            $"/channels/{channel.Slug}", new ChannelResponse(channel.Id, channel.Name, channel.Slug, channel.Description));
    }

    private static Dictionary<string, string[]> Validate(CreateChannelRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > Channel.NameMaxLength)
        {
            errors["name"] = [$"name must be 1 to {Channel.NameMaxLength} characters."];
        }

        if (request.Slug is null || !SlugPattern().IsMatch(request.Slug))
        {
            errors["slug"] = ["slug must be 3 to 40 characters: lower-case letters, digits and hyphens."];
        }

        return errors;
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
