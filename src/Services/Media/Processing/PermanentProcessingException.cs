namespace NewStore.Media.Processing;

/// <summary>
/// The file itself is the problem, so another attempt would fail the same way: the job and the upload fail at once.
/// <see cref="Reason"/> is the short code the creator sees (<c>unreadable</c>, <c>encode-failed</c>, ...); the message is the detail
/// that goes to the job's <c>last_error</c>.
/// </summary>
public sealed class PermanentProcessingException(string reason, string detail) : Exception(detail)
{
    public string Reason { get; } = reason;
}
