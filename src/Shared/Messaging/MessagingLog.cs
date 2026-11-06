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
}
