namespace NewStore.Messaging;

/// <summary>
/// Handles one kind of event. A handler needs no idempotency code of its own: <c>IdempotentDispatcher</c>
/// calls it at most once per message id.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The name is the project's vocabulary: a handler of integration events, not a .NET event handler.")]
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    /// <summary>
    /// Runs inside the inbox transaction on the consumer's <c>DbContext</c>, so its writes commit with the inbox
    /// row. The dispatcher calls <c>SaveChangesAsync</c> after the handler returns; a handler that throws
    /// leaves no trace, not even the inbox row, and the message is tried again.
    /// </summary>
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}
