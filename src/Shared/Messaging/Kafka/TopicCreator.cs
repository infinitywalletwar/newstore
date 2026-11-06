using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NewStore.Messaging.Kafka;

/// <summary>
/// Creates the topics a service declared with <c>AddTopic</c>, and the dead-letter topic of every topic it
/// handles, before the relay and the consumer start. Register it before them: hosted services start in order.
/// </summary>
internal sealed class TopicCreator(
    MessagingRegistry registry, IOptions<MessagingOptions> options, ILogger<TopicCreator> logger) : IHostedService
{
    private const int Attempts = 5;
    private static readonly TimeSpan s_pause = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // One dead-letter topic with one partition per handled topic: a failed message is an exception, not a stream.
        var deadLetters = registry.HandledTopics
            .Select(t => new TopicRegistration(t + options.Value.DeadLetterSuffix, 1));
        var specifications = registry.Topics.Concat(deadLetters)
            .DistinctBy(t => t.Name)
            .Select(t => new TopicSpecification { Name = t.Name, NumPartitions = t.Partitions, ReplicationFactor = -1 })
            .ToList();

        if (specifications.Count == 0)
        {
            return;
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            ClientId = options.Value.ClientId,
        }).Build();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateAsync(admin, specifications);
                return;
            }
            catch (Exception ex) when (ex is KafkaException or CreateTopicsException)
            {
                // The broker may still be starting.
                if (attempt == Attempts)
                {
                    MessagingLog.TopicCreationGaveUp(logger, ex);
                    return;
                }

                MessagingLog.TopicCreationFailed(logger, attempt, Attempts, ex.Message);
                await Task.Delay(s_pause, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CreateAsync(IAdminClient admin, List<TopicSpecification> specifications)
    {
        try
        {
            await admin.CreateTopicsAsync(specifications);
        }
        catch (CreateTopicsException ex)
        {
            // A topic that exists already is the normal case on every start but the first.
            var failures = ex.Results.Where(r => r.Error.Code is not ErrorCode.TopicAlreadyExists and not ErrorCode.NoError).ToList();
            foreach (var created in ex.Results.Where(r => r.Error.Code == ErrorCode.NoError))
            {
                MessagingLog.TopicCreated(logger, created.Topic);
            }

            if (failures.Count > 0)
            {
                throw;
            }
        }
    }
}
