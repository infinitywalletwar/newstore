namespace NewStore.Messaging.Inbox;

/// <summary>Proof that a consumer handled a message. The key is <c>(Consumer, MessageId)</c>, so each consumer group has its own set.</summary>
public sealed class InboxMessage
{
    /// <summary>The consumer group id.</summary>
    public required string Consumer { get; set; }

    public Guid MessageId { get; set; }

    public DateTimeOffset HandledAt { get; set; }
}
