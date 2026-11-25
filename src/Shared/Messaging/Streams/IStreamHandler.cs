namespace NewStore.Messaging;

/// <summary>
/// Handles a batch of stream messages, registered with <see cref="MessagingBuilder.AddStreamHandler{TMessage, THandler}"/>.
/// There is no inbox: a batch can arrive again after a crash or a failed commit, so the handler is idempotent by a natural
/// key. It throws only when the whole batch should be retried (a database that is down); one bad message is dropped and logged.
/// </summary>
public interface IStreamHandler<TMessage>
    where TMessage : class
{
    Task HandleAsync(IReadOnlyList<TMessage> batch, CancellationToken cancellationToken);
}
