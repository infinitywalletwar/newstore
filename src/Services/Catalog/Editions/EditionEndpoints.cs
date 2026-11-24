using Microsoft.EntityFrameworkCore;

using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Editions;

public sealed record EditionView(Guid Id, string Format, decimal Price, int CopiesTotal, int CopiesLeft, bool OnSale);

public static class EditionEndpoints
{
    public static IEndpointRouteBuilder MapEditionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/videos/{id:guid}/editions", async (CatalogDbContext db, Guid id, CancellationToken ct) =>
        {
            // A draft is a 404 here as on GET /videos/{id}: a draft is one only its creator sees (V-1).
            if (!await db.Videos.AnyAsync(v => v.Id == id && v.Status == VideoStatus.Published, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Video not found.");
            }

            // Not cached: a cached list would add its lifetime to the lag of copies_left (ADR 0011, point 6).
            var editions = await db.Editions
                .AsNoTracking()
                .Where(e => e.VideoId == id)
                .OrderBy(e => e.Price).ThenBy(e => e.Id)
                .Select(e => new EditionView(
                    e.Id, e.Format, e.Price, e.CopiesTotal, e.CopiesLeft, e.EndedAt == null && e.CopiesLeft > 0))
                .ToListAsync(ct);
            return Results.Ok(editions);
        });

        return app;
    }
}
