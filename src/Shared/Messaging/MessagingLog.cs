using Microsoft.Extensions.Logging;

namespace NewStore.Messaging;

/// <summary>Log messages of the library, generated at build time (analyzer CA1848).</summary>
internal static partial class MessagingLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay published {Published} message(s); {Failed} failed.")]
    public static partial void RelayBatch(ILogger logger, int published, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} for {Topic} was not sent (attempt {Attempts}): {Error}")]
    public static partial void RelayMessageFailed(ILogger logger, Guid messageId, string topic, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The outbox relay loop failed; it tries again after the interval.")]
    public static partial void RelayLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created topic {Topic}.")]
    public static partial void TopicCreated(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creating topics failed (attempt {Attempt} of {Max}): {Error}")]
    public static partial void TopicCreationFailed(ILogger logger, int attempt, int max, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Topics could not be created; the service starts anyway and the relay keeps retrying.")]
    public static partial void TopicCreationGaveUp(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Message {MessageId} on {Topic} was handled before; skipped.")]
    public static partial void DuplicateSkipped(ILogger logger, Guid messageId, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The handler failed for {Topic}[{Partition}]@{Offset} (attempt {Attempt} of {Max}).")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, string topic, int partition, long offset, int attempt, int max);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {Topic}[{Partition}]@{Offset} moved to {DeadLetterTopic}: {Reason}")]
    public static partial void DeadLettered(ILogger logger, string topic, int partition, long offset, string deadLetterTopic, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {Topic}[{Partition}]@{Offset} has type '{Type}', which has no handler on this topic; skipped.")]
    public static partial void UnknownType(ILogger logger, string topic, int partition, long offset, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consuming failed: {Error}")]
    public static partial void ConsumeFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing {Topic}[{Partition}]@{Offset} failed outside the handler; the message is read again.")]
    public static partial void ProcessingFailed(ILogger logger, Exception exception, string topic, int partition, long offset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Committing offsets failed: {Error}")]
    public static partial void CommitFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Outbox} outbox and {Inbox} inbox rows older than their retention")]
    public static partial void CleanupDeleted(ILogger logger, int outbox, int inbox);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cleanup found no outbox or inbox rows past their retention.")]
    public static partial void CleanupNothingToDelete(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The cleanup pass failed; it tries again after the interval.")]
    public static partial void CleanupFailed(ILogger logger, Exception exception);
}
