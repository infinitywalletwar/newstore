namespace NewStore.Media;

/// <summary>Who may download which video's original. Task 6 writes it from <c>CopyAssigned</c> events.</summary>
public sealed class DigitalCopy
{
    public Guid OwnerId { get; set; }
    public Guid VideoId { get; set; }
    public Guid EditionId { get; set; }
    public int CopyNumber { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}
