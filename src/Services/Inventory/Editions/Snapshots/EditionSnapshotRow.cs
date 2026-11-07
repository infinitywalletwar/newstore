namespace NewStore.Inventory;

/// <summary>
/// The state of one edition as the last <see cref="EditionSnapshots"/> pass sent it (ADR 0011). The next pass sends
/// the edition again only when <c>editions</c> differs from this row.
/// </summary>
public sealed class EditionSnapshotRow
{
    public Guid EditionId { get; init; }

    /// <summary>1 for the first snapshot of the edition, then one more for each later one.</summary>
    public long Version { get; init; }

    public decimal Price { get; init; }
    public int CopiesTotal { get; init; }
    public int CopiesLeft { get; init; }
    public DateTimeOffset? EndedAt { get; init; }

    public DateTimeOffset PublishedAt { get; init; }
}
