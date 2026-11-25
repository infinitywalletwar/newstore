using System.Diagnostics;
using System.Text.Json;

using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NewStore.Messaging.Kafka;
using NewStore.Messaging.Telemetry;

namespace NewStore.Messaging.Streams;

/// <summary>
/// Consumes one stream topic in batches, one instance per <c>AddStreamHandler</c>, in the consumer group
/// <c>&lt;ClientId&gt;.&lt;topic&gt;</c>. It gathers messages until it holds <see cref="MessagingOptions.StreamBatchSize"/>
/// or <see cref="MessagingOptions.StreamBatchWindow"/> has passed since the batch's first message, hands the readable ones
/// to <see cref="IStreamHandler{TMessage}"/>, and then commits each partition's highest offset + 1.
/// There is no inbox and no dead-letter topic: a message of another type or one that does not deserialise is skipped and
/// logged, and a batch whose handler throws is read again after a pause, without a limit. A re-read is harmless because
/// the handler is idempotent by a natural key (AGENTS.md, Streams).
/// </summary>
internal sealed class KafkaStreamConsumer<TMessage>(
    string topic,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<MessagingOptions> options,
    ILogger<KafkaStreamConsumer<TMessage>> logger) : BackgroundService
    where TMessage : class
{
    private static readonly string s_typeName = typeof(TMessage).FullName!;
    private static readonly TimeSpan s_pollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_consumeRetryPause = TimeSpan.FromSeconds(1);

    // Only the consumer thread touches these fields: the rebalance handlers run inside Consume, on the same thread.

    // Every message read since the last handler call, readable or not, so the commit covers the skipped ones too.
    private readonly List<Gathered> _batch = [];

    // Handled offsets that are not committed yet: a failed commit leaves them here, and the next commit sends them.
    private readonly Dictionary<TopicPartition, Offset> _uncommitted = [];

    private long _batchStartedAt;
    private int _failedAttempts;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume blocks its thread, so the loop gets a thread of its own instead of a thread-pool thread.
        Task.Factory.StartNew(
            () => Run(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = settings.ClientId,
            // A group of its own: a slow event handler never holds up the stream, and a stream batch never holds up events.
            GroupId = $"{settings.ClientId}.{topic}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        })
            .SetPartitionsRevokedHandler((c, revoked) =>
            {
                // Still the owner: commit what was handled, then drop the unhandled rest. The new owner reads it.
                Commit(c);
                Forget(revoked.Select(r => r.TopicPartition));
            })
            // Lost: another member may own the partitions already, so no commit.
            .SetPartitionsLostHandler((_, lost) => Forget(lost.Select(l => l.TopicPartition)))
            .Build();

        consumer.Subscribe(topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(NextPollTimeout(settings));
                }
                catch (ConsumeException ex)
                {
                    MessagingLog.ConsumeFailed(logger, ex.Error.Reason);
                    Pause(s_consumeRetryPause, stoppingToken);
                    continue;
                }

                if (result is not null)
                {
                    Gather(result);
                }

                if (_batch.Count > 0
                    && (_batch.Count >= settings.StreamBatchSize || timeProvider.GetElapsedTime(_batchStartedAt) >= settings.StreamBatchWindow))
                {
                    HandleBatch(consumer, settings, stoppingToken);
                }
            }
        }
        finally
        {
            // Stopping: a batch still being gathered is not handled, and the next start reads it again.
            Commit(consumer);
            consumer.Close();
        }
    }

    /// <summary>While a batch is open, wait no longer than what is left of its window.</summary>
    private TimeSpan NextPollTimeout(MessagingOptions settings)
    {
        if (_batch.Count == 0)
        {
            return s_pollTimeout;
        }

        var left = settings.StreamBatchWindow - timeProvider.GetElapsedTime(_batchStartedAt);
        return left <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(left.Ticks, s_pollTimeout.Ticks));
    }

    private void Gather(ConsumeResult<string, string> result)
    {
        if (_batch.Count == 0)
        {
            _batchStartedAt = timeProvider.GetTimestamp();
        }

        _batch.Add(new Gathered(result.TopicPartitionOffset, Read(result)));
    }

    /// <summary>The message, or null when it is of another type or cannot be read. Retrying would not change either.</summary>
    private TMessage? Read(ConsumeResult<string, string> result)
    {
        var typeName = result.Message.Headers.GetString(MessageHeaders.Type);
        if (typeName != s_typeName)
        {
            MessagingLog.StreamTypeSkipped(logger, result.Topic, result.Partition.Value, result.Offset.Value, typeName ?? "(none)", s_typeName);
            return null;
        }

        try
        {
            if (result.Message.Value is { } value && JsonSerializer.Deserialize<TMessage>(value) is { } message)
            {
                return message;
            }

            MessagingLog.StreamMessageUnreadable(logger, null, result.Topic, result.Partition.Value, result.Offset.Value, s_typeName);
        }
        catch (JsonException ex)
        {
            MessagingLog.StreamMessageUnreadable(logger, ex, result.Topic, result.Partition.Value, result.Offset.Value, s_typeName);
        }

        return null;
    }

    private void HandleBatch(IConsumer<string, string> consumer, MessagingOptions settings, CancellationToken stoppingToken)
    {
        var messages = _batch.Where(g => g.Message is not null).Select(g => g.Message!).ToList();
        if (messages.Count > 0)
        {
            Exception? failure = null;

            // The span covers the handler only: it ends before a retry's seek and pause.
            using (var activity = MessagingTelemetry.StartProcessBatch(topic, messages.Count))
            {
                try
                {
                    InvokeHandlerAsync(messages, stoppingToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Stopped inside the handler: nothing is committed, so the next start reads the batch again.
                    _batch.Clear();
                    return;
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    failure = ex;
                }
            }

            if (failure is not null)
            {
                ReadAgainLater(consumer, settings, failure, messages.Count, stoppingToken);
                return;
            }
        }

        _failedAttempts = 0;

        // A partition's messages arrive in offset order, so the last one written per partition is its highest.
        foreach (var gathered in _batch)
        {
            _uncommitted[gathered.Position.TopicPartition] = gathered.Position.Offset + 1;
        }

        _batch.Clear();
        Commit(consumer);
    }

    private async Task InvokeHandlerAsync(List<TMessage> messages, CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStreamHandler<TMessage>>().HandleAsync(messages, stoppingToken);
    }

    /// <summary>
    /// The handler threw: seek every partition of the batch back to its first offset in the batch, so the next polls read
    /// the same messages again, and wait. A seek that fails is logged, not thrown. The pause is at most
    /// <see cref="MessagingOptions.MaxTransientRetryPause"/>, far below <c>max.poll.interval.ms</c>, so the consumer stays
    /// in its group however long the failure lasts.
    /// </summary>
    private void ReadAgainLater(
        IConsumer<string, string> consumer, MessagingOptions settings, Exception exception, int count, CancellationToken stoppingToken)
    {
        _failedAttempts++;
        var pause = settings.TransientPauseFor(_failedAttempts);
        MessagingLog.StreamBatchFailed(logger, exception, count, topic, _failedAttempts, pause);

        foreach (var first in _batch.GroupBy(g => g.Position.TopicPartition).Select(p => p.First().Position))
        {
            try
            {
                consumer.Seek(first);
            }
            catch (KafkaException ex)
            {
                // An exception out of Run would end the hosted service, and by default the host with it. Log and keep
                // polling: this partition's position stays past the batch, so those messages may not be read again.
                MessagingLog.StreamSeekFailed(logger, first.Topic, first.Partition.Value, first.Offset.Value, ex.Error.Reason);
            }
        }

        _batch.Clear();
        Pause(pause, stoppingToken);
    }

    private void Commit(IConsumer<string, string> consumer)
    {
        if (_uncommitted.Count == 0)
        {
            return;
        }

        try
        {
            consumer.Commit(_uncommitted.Select(u => new TopicPartitionOffset(u.Key, u.Value)));
            _uncommitted.Clear();
        }
        catch (KafkaException ex)
        {
            // Not fatal, and the batch is not handled again: the offsets stay here and the next commit sends them.
            MessagingLog.CommitFailed(logger, ex.Error.Reason);
        }
    }

    private void Forget(IEnumerable<TopicPartition> partitions)
    {
        var gone = partitions.ToHashSet();
        _batch.RemoveAll(g => gone.Contains(g.Position.TopicPartition));
        foreach (var partition in gone)
        {
            _uncommitted.Remove(partition);
        }
    }

    private static void Pause(TimeSpan duration, CancellationToken stoppingToken) =>
        stoppingToken.WaitHandle.WaitOne(duration);

    /// <summary>A message read into the batch: where it was, and what it holds (null when it was skipped).</summary>
    private readonly record struct Gathered(TopicPartitionOffset Position, TMessage? Message);
}
