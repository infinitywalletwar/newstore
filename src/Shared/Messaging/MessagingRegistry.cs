namespace NewStore.Messaging;

internal sealed record TopicRegistration(string Name, int Partitions);

/// <summary>What the service declared in <c>AddMessaging</c>: its topics and, from step 2 on, its handlers.</summary>
internal sealed class MessagingRegistry
{
    private readonly List<TopicRegistration> _topics = [];

    public IReadOnlyList<TopicRegistration> Topics => _topics;

    public void AddTopic(string name, int partitions) => _topics.Add(new TopicRegistration(name, partitions));
}
