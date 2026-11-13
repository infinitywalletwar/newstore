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
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/end-sale", EndSaleAsync);
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
            CreatedAt = time.GetUtcNowToMicrosecond(),
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

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateEditionRequest request, InventoryDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        // One conditional UPDATE, no read first. Under Read Committed PostgreSQL re-evaluates the WHERE of an
        // UPDATE that waited for a concurrent update of the same row, so a reserve that sets locked_at
        // first makes this UPDATE match nothing (ADR 0010).
        // Unlocked means no reservation ever existed, so copies_left == copies_total: both move together.
        var updated = await db.Editions
            .Where(e => e.Id == id && e.LockedAt == null && e.EndedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.Price, e => request.Price ?? e.Price)
                .SetProperty(e => e.CopiesTotal, e => request.Copies ?? e.CopiesTotal)
                .SetProperty(e => e.CopiesLeft, e => request.Copies ?? e.CopiesLeft), ct);

        var edition = await db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (edition is null)
        {
            return Problems.NotFound("Edition");
        }

        if (updated == 1)
        {
            return Results.Ok(EditionResponse.From(edition));
        }

        return edition.EndedAt is not null
            ? Problems.Conflict("sale-ended", "This edition's sale has ended.")
            : Problems.Conflict("edition-locked", "The price and the number of copies are fixed once a copy is reserved.");
    }

    private static async Task<IResult> EndSaleAsync(Guid id, InventoryDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNowToMicrosecond();

        // Once: the second call matches nothing and changes nothing, and still answers 200.
        // Pending reservations made earlier stay valid; confirm does not look at ended_at.
        await db.Editions
            .Where(e => e.Id == id && e.EndedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.EndedAt, now)
                .SetProperty(e => e.CopiesLeft, 0), ct);

        return await db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } edition
            ? Results.Ok(EditionResponse.From(edition))
            : Problems.NotFound("Edition");
    }

    private static Dictionary<string, string[]> Validate(UpdateEditionRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Price is null && request.Copies is null)
        {
            errors["request"] = ["price or copies is required."];
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
