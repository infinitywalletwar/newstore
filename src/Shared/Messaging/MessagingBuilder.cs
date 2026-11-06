namespace NewStore.Messaging;

/// <summary>Declares a service's topics and handlers in <c>AddMessaging</c>.</summary>
public sealed class MessagingBuilder
{
    private readonly MessagingRegistry _registry;

    internal MessagingBuilder(MessagingRegistry registry) => _registry = registry;

    /// <summary>Declares a topic that is created at startup when it is missing.</summary>
    public MessagingBuilder AddTopic(string name, int partitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitions, 1);

        _registry.AddTopic(name, partitions);
        return this;
    }
}
