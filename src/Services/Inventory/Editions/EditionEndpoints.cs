using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using NewStore.Messaging;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Inventory;

public static partial class EditionEndpoints
{
    public const int MaxCopies = 10_000;

    public static IEndpointRouteBuilder MapEditionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/editions");
        // Reading an edition is public. Everything else needs a signed-in user, and the ones on an existing
        // edition also check that the user is its creator (AuthorizeCreatorAsync).
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization(Policies.SignedIn);
        group.MapGet("/{id:guid}/copies", GetCopiesAsync).RequireAuthorization(Policies.SignedIn);
        group.MapPatch("/{id:guid}", UpdateAsync).RequireAuthorization(Policies.SignedIn);
        group.MapPost("/{id:guid}/end-sale", EndSaleAsync).RequireAuthorization(Policies.SignedIn);
        return routes;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalog could not say who owns video {VideoId}.")]
    private static partial void LogCatalogUnavailable(ILogger logger, Exception exception, Guid videoId);

    /// <summary>
    /// Loads the edition and asks <see cref="EditionOwnerHandler"/> whether the user is its creator. Returns the reply to send
    /// when not: 404 for an unknown edition, 403 for someone else's. Null means go on.
    /// </summary>
    private static async Task<IResult?> AuthorizeCreatorAsync(
        Guid id, ClaimsPrincipal user, IAuthorizationService authorization, InventoryDbContext db, CancellationToken ct)
    {
        var edition = await db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (edition is null)
        {
            return Problems.NotFound("Edition");
        }

        // The edition may change before the write below, but its creator never does, so a stale read cannot let the wrong user in.
        var result = await authorization.AuthorizeAsync(user, edition, new EditionOwnerRequirement());
        return result.Succeeded
            ? null
            : Problems.Forbidden("not-your-edition", "This edition belongs to another creator.");
    }

    private static async Task<IResult> CreateAsync(
        CreateEditionRequest request, ClaimsPrincipal user, InventoryDbContext db, CatalogClient catalog, TimeProvider time,
        ILogger<CatalogClient> logger, CancellationToken ct)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return Problems.Validation(errors);
        }

        Guid? videoOwner;
        try
        {
            videoOwner = await catalog.GetVideoOwnerAsync(request.VideoId, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TransientFailureException)
        {
            LogCatalogUnavailable(logger, e, request.VideoId);
            return Problems.Unavailable("dependency-unavailable", "The catalog is not reachable. Try again shortly.");
        }

        if (videoOwner is null)
        {
            return Problems.Validation(new() { ["videoId"] = ["videoId names no video."] });
        }

        var creatorId = user.UserId();
        if (videoOwner != creatorId)
        {
            return Problems.Forbidden("not-your-video", "This video belongs to another creator.");
        }

        var edition = new Edition
        {
            Id = Guid.CreateVersion7(),
            VideoId = request.VideoId,
            CreatorId = creatorId,
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

    private static async Task<IResult> GetCopiesAsync(
        Guid id, ClaimsPrincipal user, IAuthorizationService authorization, InventoryDbContext db, CancellationToken ct)
    {
        if (await AuthorizeCreatorAsync(id, user, authorization, db, ct) is { } refusal)
        {
            return refusal;
        }

        var copies = await db.Copies
            .Where(c => c.EditionId == id)
            .OrderBy(c => c.Number)
            .Select(c => new CopyResponse(c.Number, c.OwnerId, c.AssignedAt))
            .ToListAsync(ct);
        return Results.Ok(copies);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateEditionRequest request, ClaimsPrincipal user, IAuthorizationService authorization,
        InventoryDbContext db, CancellationToken ct)
    {
        if (await AuthorizeCreatorAsync(id, user, authorization, db, ct) is { } refusal)
        {
            return refusal;
        }

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

    private static async Task<IResult> EndSaleAsync(
        Guid id, ClaimsPrincipal user, IAuthorizationService authorization, InventoryDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        if (await AuthorizeCreatorAsync(id, user, authorization, db, ct) is { } refusal)
        {
            return refusal;
        }

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
