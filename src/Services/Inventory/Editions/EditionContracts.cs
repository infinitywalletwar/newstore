namespace NewStore.Inventory;

public sealed record CreateEditionRequest(Guid VideoId, Guid CreatorId, string? Format, decimal Price, int Copies);

public sealed record EditionResponse(
    Guid Id, Guid VideoId, Guid CreatorId, string Format, decimal Price, int CopiesTotal, int CopiesLeft, int CopiesSold)
{
    public static EditionResponse From(Edition e) =>
        new(e.Id, e.VideoId, e.CreatorId, e.Format.ToString(), e.Price, e.CopiesTotal, e.CopiesLeft, e.CopiesAssigned);
}

public sealed record CopyResponse(int Number, Guid OwnerId, DateTimeOffset AssignedAt);
