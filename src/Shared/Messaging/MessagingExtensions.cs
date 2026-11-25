using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using NewStore.Messaging.Cleanup;
using NewStore.Messaging.Inbox;
using NewStore.Messaging.Kafka;
using NewStore.Messaging.Outbox;
using NewStore.Messaging.Streams;

namespace NewStore.Messaging;

public static class MessagingExtensions
{
    private const string LiveTag = "live";

    /// <summary>
    /// Binds <c>Messaging:*</c> (<c>BootstrapServers</c> is required; <c>ClientId</c> defaults to the
    /// application name) and registers the producer, the outbox relay with its <c>outbox-relay</c> live
    /// check, the cleanup of old outbox and inbox rows, the topic creator, the event consumer and the inbox dispatcher,
    /// <see cref="IStreamProducer"/>, and one stream consumer per <c>AddStreamHandler</c>. <typeparamref name="TContext"/> must already be registered by the
    /// service and have <c>AddOutbox()</c> (and <c>AddInbox()</c> when it consumes events) in its model.
    /// </summary>
    public static IHostApplicationBuilder AddMessaging<TContext>(
        this IHostApplicationBuilder builder, Action<MessagingBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var registry = new MessagingRegistry();
        configure(new MessagingBuilder(registry, builder.Services));

        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<MessagingOptions>()
            .BindConfiguration(MessagingOptions.SectionName)
            .PostConfigure(o => o.ClientId ??= builder.Environment.ApplicationName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Messaging:BootstrapServers is missing.")
            .Validate(o => o.OutboxRetention > TimeSpan.Zero, "Messaging:OutboxRetention must be positive.")
            .Validate(o => o.InboxRetention > TimeSpan.Zero, "Messaging:InboxRetention must be positive.")
            .Validate(o => o.CleanupInterval > TimeSpan.Zero, "Messaging:CleanupInterval must be positive.")
            .Validate(o => o.StreamBatchSize >= 1, "Messaging:StreamBatchSize must be at least 1.")
            .Validate(o => o.StreamBatchWindow > TimeSpan.Zero, "Messaging:StreamBatchWindow must be positive.")
            .ValidateOnStart();

        services.AddSingleton(registry);
        services.AddSingleton<KafkaProducer>();
        services.AddSingleton<RelayHeartbeat>();
        services.AddScoped<IOutbox, Outbox<TContext>>();
        services.AddSingleton<IStreamProducer, StreamProducer>();

        // Hosted services start in registration order: topics first.
        services.AddHostedService<TopicCreator>();
        services.AddHostedService<OutboxRelay<TContext>>();
        services.AddHostedService<MessagingCleanupService<TContext>>();
        services.AddSingleton<IdempotentDispatcher<TContext>>();
        services.AddHostedService<KafkaConsumerService<TContext>>();

        // One consumer per stream, registered after the topic creator. Each needs its topic, which the container
        // cannot supply, so it is built by a factory.
        foreach (var stream in registry.Streams)
        {
            var consumerType = typeof(KafkaStreamConsumer<>).MakeGenericType(stream.MessageType);
            services.AddSingleton<IHostedService>(sp => (IHostedService)ActivatorUtilities.CreateInstance(sp, consumerType, stream.Topic));
        }

        services.AddHealthChecks().AddCheck<OutboxRelayHealthCheck>("outbox-relay", HealthStatus.Unhealthy, [LiveTag]);

        return builder;
    }
}
