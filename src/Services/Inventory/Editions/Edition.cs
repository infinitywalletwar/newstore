namespace NewStore.Inventory;

public enum EditionFormat { Digital, Dvd }

/// <summary>A limited run of one video: <see cref="CopiesTotal"/> numbered copies for sale.</summary>
public sealed class Edition
{
    public Guid Id { get; init; }
    public Guid VideoId { get; init; }
    public Guid CreatorId { get; init; }
    public EditionFormat Format { get; init; }
    public decimal Price { get; init; }
    public int CopiesTotal { get; init; }

    /// <summary>Copies nobody holds. Down on reserve, up on release and expiry.</summary>
    public int CopiesLeft { get; set; }

    /// <summary>The last copy number handed out. Only confirm moves it.</summary>
    public int CopiesAssigned { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Set by the first successful reserve and never cleared: price and copies are fixed from then on.</summary>
    public DateTimeOffset? LockedAt { get; set; }

    /// <summary>Set once when the creator ends the sale. The unsold copies are withdrawn.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>PostgreSQL's <c>xmin</c> system column: it changes with every update of the row.</summary>
    public uint Version { get; private set; }
}
