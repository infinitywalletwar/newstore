namespace NewStore.Media;

public sealed record CreateUploadRequest(Guid VideoId, string FileName, string ContentType, long SizeBytes);

public sealed record CompleteUploadRequest(IReadOnlyList<CompletedPart> Parts);

public sealed record CompletedPart(int PartNumber, string ETag);

public sealed record UploadedPart(int PartNumber, string ETag, long Size);

/// <summary>A presigned URL for one part. <c>Size</c> is the exact slice the URL accepts: smaller for the last part.</summary>
public sealed record PartUrl(int PartNumber, string Url, long Size);

public sealed record UploadResponse(
    Guid Id, Guid VideoId, string FileName, long SizeBytes, string Status, string? FailureReason,
    long PartSize, int PartCount, IReadOnlyList<UploadedPart> UploadedParts, IReadOnlyList<PartUrl> PartUrls,
    DateTimeOffset? PartUrlsExpireAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record UploadSummary(
    Guid Id, Guid VideoId, string FileName, long SizeBytes, string Status, string? FailureReason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static UploadSummary From(Upload u) =>
        new(u.Id, u.VideoId, u.FileName, u.SizeBytes, u.Status.ToString(), u.FailureReason, u.CreatedAt, u.UpdatedAt);
}
