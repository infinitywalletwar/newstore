namespace NewStore.Messaging;

/// <summary>Bound from the <c>Messaging</c> configuration section.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public required string BootstrapServers { get; set; }

    /// <summary>Names the producer and is the consumer group id. Defaults to the service's application name.</summary>
    public string? ClientId { get; set; }

    /// <summary>The pause between two relay loops that found less than a full batch.</summary>
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int RelayBatchSize { get; set; } = 100;

    /// <summary>How long Kafka may take to acknowledge a message before the relay counts it as failed (Confluent <c>MessageTimeoutMs</c>).</summary>
    public TimeSpan ProduceTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxHandlerAttempts { get; set; } = 3;

    /// <summary>
    /// The pause before the first retry of a transient handler failure. It doubles with each attempt up to
    /// <see cref="MaxTransientRetryPause"/>. Transient failures are retried without a limit.
    /// </summary>
    public TimeSpan TransientRetryPause { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest pause between two attempts at a message that fails transiently.</summary>
    public TimeSpan MaxTransientRetryPause { get; set; } = TimeSpan.FromSeconds(30);

    public string DeadLetterSuffix { get; set; } = ".dlt";

    /// <summary>
    /// How long a published outbox row stays. A published row is only an audit trail; one day keeps the table small
    /// and leaves a day to inspect what was sent. Unpublished rows are never deleted.
    /// </summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// How long an inbox row stays. An inbox row is the proof that a message was handled, so a duplicate older than this is
    /// handled again. Seven days equals Kafka's default topic retention (<c>log.retention.hours</c> = 168): a duplicate cannot
    /// arrive from the topic after the topic dropped the message, unless someone replays older data on purpose.
    /// Raise it before such a replay.
    /// </summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The pause between two cleanup passes. The first pass runs after the first interval, not at startup.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}
