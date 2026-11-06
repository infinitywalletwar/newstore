using System.Diagnostics;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NewStore.Messaging.Kafka;
using NewStore.Messaging.Telemetry;

namespace NewStore.Messaging.Outbox;

/// <summary>
/// Moves outbox rows to Kafka. Every <see cref="MessagingOptions.RelayInterval"/> it locks a batch of unsent
/// rows, sends them, and marks the delivered ones, all in one transaction. A message that Kafka does not
/// acknowledge stays unsent and is tried again on the next loop: the relay never drops a message.
/// </summary>
internal sealed class OutboxRelay<TContext>(
    IServiceScopeFactory scopeFactory,
    KafkaProducer producer,
    RelayHeartbeat heartbeat,
    TimeProvider timeProvider,
    IOptions<MessagingOptions> options,
    ILogger<OutboxRelay<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        heartbeat.Stamp(timeProvider.GetUtcNow());

        while (!stoppingToken.IsCancellationRequested)
        {
            var drained = false;
            try
            {
                var result = await RelayBatchAsync(settings, stoppingToken);
                // A full, fully delivered batch means more rows are probably waiting: go on without a pause.
                drained = result.Published == settings.RelayBatchSize;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database may be restarting. The loop must outlive that.
                MessagingLog.RelayLoopFailed(logger, ex);
            }

            heartbeat.Stamp(timeProvider.GetUtcNow());

            if (!drained)
            {
                try
                {
                    await Task.Delay(settings.RelayInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<(int Published, int Failed)> RelayBatchAsync(MessagingOptions settings, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(stoppingToken);

        // FOR UPDATE locks the rows we read; SKIP LOCKED makes a second relay skip them instead of waiting,
        // so two relays share the table and never send the same row. {size} becomes a query parameter.
        var size = settings.RelayBatchSize;
        var batch = await context.Set<OutboxMessage>()
            .FromSql($"""
                SELECT id, topic, key, type, payload, occurred_at, trace_parent, published_at, attempts, last_error
                FROM outbox_messages
                WHERE published_at IS NULL
                ORDER BY id
                LIMIT {size}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(stoppingToken);

        if (batch.Count == 0)
        {
            return (0, 0);
        }

        // Every ProduceAsync starts before the first is awaited: the batch is in flight at once, and the
        // client keeps the order of the calls within a partition.
        var sends = batch.Select(SendAsync).ToList();
        await Task.WhenAll(sends);

        // Past this point the messages are on Kafka (or marked failed), so a stop must not roll them back:
        // no token. A crash before the commit only means a duplicate, which the consumer's inbox absorbs.
        await context.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        var published = batch.Count(m => m.PublishedAt is not null);
        MessagingLog.RelayBatch(logger, published, batch.Count - published);
        return (published, batch.Count - published);
    }

    private async Task SendAsync(OutboxMessage message)
    {
        using var activity = MessagingTelemetry.StartPublish(message.Topic, message.Id, message.TraceParent);
        try
        {
            var kafkaMessage = new Message<string, string>
            {
                Key = message.Key,
                Value = message.Payload,
                Headers = [],
            };
            kafkaMessage.Headers.Add(MessageHeaders.MessageId, message.Id.ToString());
            kafkaMessage.Headers.Add(MessageHeaders.Type, message.Type);
            kafkaMessage.Headers.Add(MessageHeaders.OccurredAt, message.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            // The consumer's span becomes a child of this publish span. Without a listener there is no span:
            // pass the request's context on unchanged.
            if ((activity?.Id ?? message.TraceParent) is { } traceParent)
            {
                kafkaMessage.Headers.Add(MessageHeaders.TraceParent, traceParent);
            }

            // No stopping token: a started send is allowed to finish, bounded by ProduceTimeout.
            await producer.ProduceAsync(message.Topic, kafkaMessage, CancellationToken.None);
            message.PublishedAt = timeProvider.GetUtcNow();
            message.LastError = null;
        }
        catch (Exception ex) when (ex is ProduceException<string, string> or KafkaException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            message.Attempts++;
            message.LastError = ex.Message;
            MessagingLog.RelayMessageFailed(logger, message.Id, message.Topic, message.Attempts, ex.Message);
        }
    }
}
