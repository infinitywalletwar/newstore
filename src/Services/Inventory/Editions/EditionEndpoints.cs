using Microsoft.EntityFrameworkCore;

namespace NewStore.Inventory;

public static class EditionEndpoints
{
    public const int MaxCopies = 10_000;

    public static IEndpointRouteBuilder MapEditionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/editions");
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/copies", GetCopiesAsync);
        return routes;
    }

    private static async Task<IResult> CreateAsync(
        CreateEditionRequest request, InventoryDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        var edition = new Edition
        {
            Id = Guid.CreateVersion7(),
            VideoId = request.VideoId,
            CreatorId = request.CreatorId,
            Format = Enum.Parse<EditionFormat>(request.Format!, ignoreCase: true),
            Price = request.Price,
            CopiesTotal = request.Copies,
            CopiesLeft = request.Copies,
            CreatedAt = time.GetUtcNow(),
        };
        db.Editions.Add(edition);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/editions/{edition.Id}", EditionResponse.From(edition));
    }

    private static async Task<IResult> GetAsync(Guid id, InventoryDbContext db, CancellationToken ct) =>
        await db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } edition
            ? Results.Ok(EditionResponse.From(edition))
            : Problems.NotFound("Edition");

    private static async Task<IResult> GetCopiesAsync(Guid id, InventoryDbContext db, CancellationToken ct)
    {
        if (!await db.Editions.AnyAsync(e => e.Id == id, ct))
        {
            return Problems.NotFound("Edition");
        }

        var copies = await db.Copies
            .Where(c => c.EditionId == id)
            .OrderBy(c => c.Number)
            .Select(c => new CopyResponse(c.Number, c.OwnerId, c.AssignedAt))
            .ToListAsync(ct);
        return Results.Ok(copies);
    }

    private static Dictionary<string, string[]> Validate(CreateEditionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.VideoId == Guid.Empty)
        {
            errors["videoId"] = ["videoId is required."];
        }

        if (request.CreatorId == Guid.Empty)
        {
            errors["creatorId"] = ["creatorId is required."];
        }

        // TryParse also accepts numbers such as "7"; IsDefined rejects those that name no member.
        if (!Enum.TryParse<EditionFormat>(request.Format, ignoreCase: true, out var format) || !Enum.IsDefined(format))
        {
            errors["format"] = [$"format must be one of: {string.Join(", ", Enum.GetNames<EditionFormat>())}."];
        }

        if (request.Price < 0)
        {
            errors["price"] = ["price must not be negative."];
        }

        if (request.Copies is < 1 or > MaxCopies)
        {
            errors["copies"] = [$"copies must be between 1 and {MaxCopies}."];
        }

        return errors;
    }
}
