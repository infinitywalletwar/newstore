namespace NewStore.Inventory;

/// <summary>There is no creator here: <see cref="Edition.CreatorId"/> is the token's subject.</summary>
public sealed record CreateEditionRequest(Guid VideoId, string? Format, decimal Price, int Copies);

public sealed record UpdateEditionRequest(decimal? Price, int? Copies);

public sealed record EditionResponse(
    Guid Id, Guid VideoId, Guid CreatorId, string Format, decimal Price, int CopiesTotal, int CopiesLeft, int CopiesSold,
    DateTimeOffset? LockedAt, DateTimeOffset? EndedAt)
{
    public static EditionResponse From(Edition e) =>
        new(e.Id, e.VideoId, e.CreatorId, e.Format.ToString(), e.Price, e.CopiesTotal, e.CopiesLeft, e.CopiesAssigned, e.LockedAt, e.EndedAt);
}

public sealed record CopyResponse(int Number, Guid OwnerId, DateTimeOffset AssignedAt);
