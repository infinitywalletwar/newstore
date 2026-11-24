using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewStore.Messaging;

/// <summary>Declares a service's topics and handlers in <c>AddMessaging</c>.</summary>
public sealed class MessagingBuilder
{
    private readonly MessagingRegistry _registry;
    private readonly IServiceCollection _services;

    internal MessagingBuilder(MessagingRegistry registry, IServiceCollection services)
    {
        _registry = registry;
        _services = services;
    }

    /// <summary>Declares a topic that is created at startup when it is missing.</summary>
    public MessagingBuilder AddTopic(string name, int partitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitions, 1);

        _registry.AddTopic(name, partitions);
        return this;
    }

    /// <summary>
    /// Subscribes the service to <paramref name="topic"/> and routes messages whose <c>type</c> header is
    /// <typeparamref name="TEvent"/>'s full name to <typeparamref name="THandler"/>. The topic's dead-letter
    /// topic is created at startup too.
    /// </summary>
    public MessagingBuilder AddHandler<TEvent, THandler>(string topic)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _services.TryAddScoped<IIntegrationEventHandler<TEvent>, THandler>();
        _registry.AddHandler(new HandlerRegistration(
            topic,
            typeof(TEvent).FullName!,
            async (scope, payload, cancellationToken) =>
            {
                var integrationEvent = JsonSerializer.Deserialize<TEvent>(payload)
                    ?? throw new JsonException($"The payload of a {typeof(TEvent).Name} was null.");
                await scope.GetRequiredService<IIntegrationEventHandler<TEvent>>()
                    .HandleAsync(integrationEvent, cancellationToken);
            }));
        return this;
    }

    /// <summary>
    /// Declares an event type on a shared <paramref name="topic"/> that this service does not handle. The consumer skips it
    /// with a Debug log instead of the Warning it logs for a type nobody declared, and writes no inbox row.
    /// </summary>
    public MessagingBuilder AddIgnoredType<TEvent>(string topic)
        where TEvent : IIntegrationEvent
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _registry.AddIgnoredType(topic, typeof(TEvent).FullName!);
        return this;
    }

    /// <summary>
    /// Consumes the stream <paramref name="topic"/> in batches with <typeparamref name="THandler"/>, in a consumer group of
    /// its own (<c>&lt;ClientId&gt;.&lt;topic&gt;</c>), without the inbox and without a dead-letter topic. Messages whose
    /// <c>type</c> header is not <typeparamref name="TMessage"/>'s full name are skipped. Declare the topic with
    /// <see cref="AddTopic"/> too.
    /// </summary>
    public MessagingBuilder AddStreamHandler<TMessage, THandler>(string topic)
        where TMessage : class
        where THandler : class, IStreamHandler<TMessage>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        _services.TryAddScoped<IStreamHandler<TMessage>, THandler>();
        _registry.AddStream(new StreamRegistration(topic, typeof(TMessage)));
        return this;
    }
}
