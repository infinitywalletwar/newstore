using Microsoft.EntityFrameworkCore;

using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Channels;

public static class ChannelEndpoints
{
    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/channels/{slug}", async (
            CatalogDbContext db, string slug, CancellationToken ct,
            int page = 1, int pageSize = Paging.DefaultPageSize) =>
        {
            if (Paging.Validate(page, pageSize) is { } errors)
            {
                return Results.ValidationProblem(errors);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.Slug == slug, ct);
            if (channel is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Channel not found.");
            }

            var videos = await VideoQueries.OfChannelAsync(db, channel.Id, channel.Name, channel.Slug, page, pageSize, ct);
            return Results.Ok(new ChannelPage(channel.Id, channel.Name, channel.Slug, channel.Description, videos));
        });

        return app;
    }
}
