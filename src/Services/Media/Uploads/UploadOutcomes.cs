namespace NewStore.Media;

public abstract record CreateOutcome
{
    public sealed record Created(Upload Upload) : CreateOutcome;

    /// <summary>The video already has a live upload.</summary>
    public sealed record Exists(Guid UploadId) : CreateOutcome;

    public sealed record StorageUnavailable : CreateOutcome;
}

public abstract record StateOutcome
{
    public sealed record Ok(UploadResponse Response) : StateOutcome;
    public sealed record NotFound : StateOutcome;

    /// <summary>S3 has forgotten the upload and there is no object. Nothing was written.</summary>
    public sealed record Lost : StateOutcome;
    public sealed record StorageUnavailable : StateOutcome;
}

public abstract record CompleteOutcome
{
    /// <summary>The upload as it is now: moved to Processing by this call, or already past Uploading, or Failed.</summary>
    public sealed record Done(Upload Upload) : CompleteOutcome;

    public sealed record NotFound : CompleteOutcome;
    public sealed record Expired : CompleteOutcome;

    /// <summary>S3 has forgotten the upload and there is no object. The row is <c>Expired</c> with <c>upload-lost</c>.</summary>
    public sealed record Lost : CompleteOutcome;

    /// <summary>The parts list does not name 1 to PartCount once each; the message names what is missing or repeated.</summary>
    public sealed record BadParts(string Message) : CompleteOutcome;

    /// <summary>S3 refused the parts or the ETags (InvalidPart, InvalidPartOrder, EntityTooSmall).</summary>
    public sealed record InvalidParts(string Message) : CompleteOutcome;

    public sealed record StorageUnavailable : CompleteOutcome;
}

public enum CancelOutcome { Done, NotFound, NotUploading }
