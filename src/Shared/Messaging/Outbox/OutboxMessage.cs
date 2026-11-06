namespace NewStore.Messaging.Outbox;

/// <summary>One event waiting to be sent. The row is written in the business transaction; the relay sends it later.</summary>
public sealed class OutboxMessage
{
    /// <summary>The event's <see cref="IIntegrationEvent.EventId"/>, which is also the Kafka <c>message-id</c> header.</summary>
    public Guid Id { get; set; }

    public required string Topic { get; set; }

    public required string Key { get; set; }

    /// <summary>The CLR type's full name. A consumer picks the class to deserialise into from it.</summary>
    public required string Type { get; set; }

    /// <summary>The event as JSON, in a <c>jsonb</c> column.</summary>
    public required string Payload { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The W3C <c>traceparent</c> of the request that enqueued the event, or null when there was none.</summary>
    public string? TraceParent { get; set; }

    /// <summary>Null until Kafka acknowledged the message.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
