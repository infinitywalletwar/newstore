namespace NewStore.Messaging;

/// <summary>An event that crosses a service boundary through Kafka.</summary>
public interface IIntegrationEvent
{
    /// <summary>The message id. It is the outbox row's key and the inbox key, so a consumer can spot a duplicate.</summary>
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}
