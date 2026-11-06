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

    public string DeadLetterSuffix { get; set; } = ".dlt";
}
