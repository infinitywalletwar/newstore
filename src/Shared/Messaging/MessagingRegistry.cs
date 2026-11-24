namespace NewStore.Messaging;

internal sealed record TopicRegistration(string Name, int Partitions);

/// <summary>Calls the handler for one event type: deserialises the payload, resolves the handler from the scope, runs it.</summary>
internal delegate Task HandlerInvoker(IServiceProvider scope, string payload, CancellationToken cancellationToken);

internal sealed record HandlerRegistration(string Topic, string TypeName, HandlerInvoker Invoke);

/// <summary>A stream topic and the message type its handler takes: one <c>KafkaStreamConsumer</c> each.</summary>
internal sealed record StreamRegistration(string Topic, Type MessageType);

/// <summary>What the service declared in <c>AddMessaging</c>: its topics, its event handlers and its streams.</summary>
internal sealed class MessagingRegistry
{
    private readonly List<TopicRegistration> _topics = [];
    private readonly Dictionary<(string Topic, string TypeName), HandlerRegistration> _handlers = [];
    private readonly List<StreamRegistration> _streams = [];

    public IReadOnlyList<TopicRegistration> Topics => _topics;

    public IReadOnlyCollection<HandlerRegistration> Handlers => _handlers.Values;

    /// <summary>
    /// The topics with an event handler: the event consumer subscribes to these, and each gets a dead-letter topic.
    /// Streams are never in it.
    /// </summary>
    public IEnumerable<string> HandledTopics => _handlers.Keys.Select(k => k.Topic).Distinct();

    /// <summary>The stream topics, each consumed by its own consumer in its own group.</summary>
    public IReadOnlyList<StreamRegistration> Streams => _streams;

    public void AddTopic(string name, int partitions) => _topics.Add(new TopicRegistration(name, partitions));

    public void AddHandler(HandlerRegistration handler) => _handlers[(handler.Topic, handler.TypeName)] = handler;

    public void AddStream(StreamRegistration stream) => _streams.Add(stream);

    public HandlerRegistration? FindHandler(string topic, string typeName) =>
        _handlers.GetValueOrDefault((topic, typeName));
}
