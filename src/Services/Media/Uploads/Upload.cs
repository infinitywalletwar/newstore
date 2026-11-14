namespace NewStore.Media;

public enum UploadStatus { Uploading, Processing, Published, Failed, Expired }

/// <summary>
/// One attempt to upload a video's file. A video has at most one live upload (<c>Uploading</c>, <c>Processing</c> or
/// <c>Published</c>); a <c>Failed</c> or <c>Expired</c> one lets the creator start again.
/// </summary>
public sealed class Upload
{
    public Guid Id { get; set; }
    public Guid VideoId { get; set; }
    public Guid CreatorId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string ObjectKey { get; set; }

    /// <summary>S3's id of the multipart upload.</summary>
    public required string S3UploadId { get; set; }

    public long PartSize { get; set; }
    public int PartCount { get; set; }
    public UploadStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public string? PlaylistPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public int? DurationSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>When the S3 multipart upload was aborted, for an upload that ended without completing. Null until then.</summary>
    public DateTimeOffset? S3AbortedAt { get; set; }

    /// <summary>PostgreSQL's <c>xmin</c>: EF checks it on every update.</summary>
    public uint Version { get; set; }
}
