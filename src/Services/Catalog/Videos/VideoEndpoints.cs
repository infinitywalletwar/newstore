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

        app.MapGet("/videos/{id:guid}", async (CatalogDbContext db, Guid id, CancellationToken ct) =>
            await VideoQueries.FindAsync(db, id, ct) is { } video
                ? Results.Ok(video)
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Video not found."));

        return app;
    }
}
