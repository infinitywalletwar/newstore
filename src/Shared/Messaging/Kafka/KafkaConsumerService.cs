using System.Diagnostics;

using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NewStore.Messaging.Inbox;

namespace NewStore.Messaging.Kafka;

/// <summary>
/// The service's one Kafka consumer. It handles one message at a time through <see cref="IdempotentDispatcher{TContext}"/>,
/// stores the offset after the handler's transaction committed, and commits the stored offsets every
/// <see cref="CommitEveryMessages"/> messages or <see cref="CommitEvery"/>. Offsets are stored only after the
/// work, so a crash re-reads at most the uncommitted tail, and the inbox turns that re-read into a no-op.
/// </summary>
internal sealed class KafkaConsumerService<TContext>(
    MessagingRegistry registry,
    IdempotentDispatcher<TContext> dispatcher,
    KafkaProducer producer,
    TimeProvider timeProvider,
    IOptions<MessagingOptions> options,
    ILogger<KafkaConsumerService<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private const int CommitEveryMessages = 100;
    private const int MaxErrorLength = 1000;
    private static readonly TimeSpan CommitEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(1);

    private int _uncommitted;
    private long _lastCommitAt;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (registry.Handlers.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Consume blocks its thread, so the loop gets a thread of its own instead of a thread-pool thread.
        return Task.Factory.StartNew(
            () => Run(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = settings.ClientId,
            GroupId = settings.ClientId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(registry.HandledTopics);
        _lastCommitAt = timeProvider.GetTimestamp();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(PollTimeout);
                }
                catch (ConsumeException ex)
                {
                    MessagingLog.ConsumeFailed(logger, ex.Error.Reason);
                    Pause(RetryPause, stoppingToken);
                    continue;
                }

                if (result is not null)
                {
                    ProcessSafely(consumer, result, settings, stoppingToken);
                }

                CommitIfDue(consumer);
            }
        }
        finally
        {
            // Stopping: the message in flight is done (the loop only checks the token between messages).
            // Commit what was stored, then leave the group so its partitions move at once.
            Commit(consumer);
            consumer.Close();
        }
    }

    private void ProcessSafely(
        IConsumer<string, string> consumer, ConsumeResult<string, string> result, MessagingOptions settings, CancellationToken stoppingToken)
    {
        try
        {
            Process(consumer, result, settings, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped between attempts. The offset was not stored, so the next start reads the message again.
        }
        catch (Exception ex)
        {
            // Something other than the handler failed, for example Kafka refused the dead-letter message.
            // The consumer's position has moved past the message, so move it back and try again.
            MessagingLog.ProcessingFailed(logger, ex, result.Topic, result.Partition.Value, result.Offset.Value);
            consumer.Seek(result.TopicPartitionOffset);
            Pause(RetryPause, stoppingToken);
        }
    }

    private void Process(
        IConsumer<string, string> consumer, ConsumeResult<string, string> result, MessagingOptions settings, CancellationToken stoppingToken)
    {
        var headers = result.Message.Headers;
        var typeName = headers.GetString(MessageHeaders.Type);

        if (!Guid.TryParse(headers.GetString(MessageHeaders.MessageId), out var messageId) || typeName is null)
        {
            // Without an id the inbox cannot dedupe, and retrying will not add one.
            DeadLetter(result, settings, "The message has no valid message-id or type header.");
            Store(consumer, result);
            return;
        }

        if (registry.FindHandler(result.Topic, typeName) is not { } handler)
        {
            // A newer producer may send an event this version does not know: additive changes must not break us.
            MessagingLog.UnknownType(logger, result.Topic, result.Partition.Value, result.Offset.Value, typeName);
            Store(consumer, result);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // CancellationToken.None: a stop lets the handler in flight finish.
                var handled = dispatcher.DispatchAsync(handler, messageId, result.Message.Value, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (!handled)
                {
                    MessagingLog.DuplicateSkipped(logger, messageId, result.Topic);
                }

                break;
            }
            catch (Exception ex)
            {
                MessagingLog.HandlerFailed(logger, ex, result.Topic, result.Partition.Value, result.Offset.Value, attempt, settings.MaxHandlerAttempts);

                if (attempt >= settings.MaxHandlerAttempts)
                {
                    DeadLetter(result, settings, Truncate($"{ex.GetType().Name}: {ex.Message}"));
                    break;
                }

                Pause(RetryPause, stoppingToken);
                stoppingToken.ThrowIfCancellationRequested();
            }
        }

        Store(consumer, result);
    }

    /// <summary>Sends the raw message to <c>&lt;topic&gt;.dlt</c> with the reason and where it came from. Throws when Kafka does not acknowledge it.</summary>
    private void DeadLetter(ConsumeResult<string, string> result, MessagingOptions settings, string reason)
    {
        var deadLetterTopic = result.Topic + settings.DeadLetterSuffix;
        var message = new Message<string, string>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = [],
        };
        foreach (var header in result.Message.Headers)
        {
            message.Headers.Add(header.Key, header.GetValueBytes());
        }

        message.Headers.Add(MessageHeaders.Error, reason);
        message.Headers.Add(MessageHeaders.OriginalTopic, result.Topic);
        message.Headers.Add(MessageHeaders.OriginalPartition, result.Partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Headers.Add(MessageHeaders.OriginalOffset, result.Offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        producer.ProduceAsync(deadLetterTopic, message, CancellationToken.None).GetAwaiter().GetResult();
        MessagingLog.DeadLettered(logger, result.Topic, result.Partition.Value, result.Offset.Value, deadLetterTopic, reason);
    }

    private void Store(IConsumer<string, string> consumer, ConsumeResult<string, string> result)
    {
        consumer.StoreOffset(result);
        _uncommitted++;
    }

    private void CommitIfDue(IConsumer<string, string> consumer)
    {
        if (_uncommitted > 0
            && (_uncommitted >= CommitEveryMessages || timeProvider.GetElapsedTime(_lastCommitAt) >= CommitEvery))
        {
            Commit(consumer);
        }
    }

    private void Commit(IConsumer<string, string> consumer)
    {
        if (_uncommitted == 0)
        {
            return;
        }

        try
        {
            consumer.Commit();
            _uncommitted = 0;
            _lastCommitAt = timeProvider.GetTimestamp();
        }
        catch (KafkaException ex)
        {
            // Not fatal: the offsets stay stored and the next commit includes them.
            MessagingLog.CommitFailed(logger, ex.Error.Reason);
        }
    }

    private static void Pause(TimeSpan duration, CancellationToken stoppingToken) =>
        stoppingToken.WaitHandle.WaitOne(duration);

    private static string Truncate(string text) =>
        text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
}
