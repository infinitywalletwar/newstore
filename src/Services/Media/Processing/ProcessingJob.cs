namespace NewStore.Media;

public enum JobStatus { Pending, Running, Succeeded, Failed }

/// <summary>The work queue's row for one upload: complete creates it, the worker (Task 5) claims it with a lease.</summary>
public sealed class ProcessingJob
{
    public Guid Id { get; set; }
    public Guid UploadId { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
