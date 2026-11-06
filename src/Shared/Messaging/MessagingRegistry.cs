namespace NewStore.Messaging;

internal sealed record TopicRegistration(string Name, int Partitions);

/// <summary>Calls the handler for one event type: deserialises the payload, resolves the handler from the scope, runs it.</summary>
internal delegate Task HandlerInvoker(IServiceProvider scope, string payload, CancellationToken cancellationToken);

internal sealed record HandlerRegistration(string Topic, string TypeName, HandlerInvoker Invoke);

/// <summary>What the service declared in <c>AddMessaging</c>: its topics and its handlers.</summary>
internal sealed class MessagingRegistry
{
    private readonly List<TopicRegistration> _topics = [];
    private readonly Dictionary<(string Topic, string TypeName), HandlerRegistration> _handlers = [];

    public IReadOnlyList<TopicRegistration> Topics => _topics;

    public IReadOnlyCollection<HandlerRegistration> Handlers => _handlers.Values;

    /// <summary>The topics with a handler: the consumer subscribes to these.</summary>
    public IEnumerable<string> HandledTopics => _handlers.Keys.Select(k => k.Topic).Distinct();

    public void AddTopic(string name, int partitions) => _topics.Add(new TopicRegistration(name, partitions));

    public void AddHandler(HandlerRegistration handler) => _handlers[(handler.Topic, handler.TypeName)] = handler;

    public HandlerRegistration? FindHandler(string topic, string typeName) =>
        _handlers.GetValueOrDefault((topic, typeName));
}
