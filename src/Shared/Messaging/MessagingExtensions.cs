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

namespace NewStore.Messaging;

public static class MessagingExtensions
{
    private const string LiveTag = "live";

    /// <summary>
    /// Binds <c>Messaging:*</c> (<c>BootstrapServers</c> is required; <c>ClientId</c> defaults to the
    /// application name) and registers the producer, the outbox relay with its <c>outbox-relay</c> live
    /// check, the cleanup of old outbox and inbox rows, the topic creator, the consumer and the inbox dispatcher. <typeparamref name="TContext"/> must already be registered by the
    /// service and have <c>AddOutbox()</c> (and <c>AddInbox()</c> when it consumes) in its model.
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
            .ValidateOnStart();

        services.AddSingleton(registry);
        services.AddSingleton<KafkaProducer>();
        services.AddSingleton<RelayHeartbeat>();
        services.AddScoped<IOutbox, Outbox<TContext>>();

        // Hosted services start in registration order: topics first.
        services.AddHostedService<TopicCreator>();
        services.AddHostedService<OutboxRelay<TContext>>();
        services.AddHostedService<MessagingCleanupService<TContext>>();
        services.AddSingleton<IdempotentDispatcher<TContext>>();
        services.AddHostedService<KafkaConsumerService<TContext>>();

        services.AddHealthChecks().AddCheck<OutboxRelayHealthCheck>("outbox-relay", HealthStatus.Unhealthy, [LiveTag]);

        return builder;
    }
}
