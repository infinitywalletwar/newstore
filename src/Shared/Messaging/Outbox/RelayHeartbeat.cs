namespace NewStore.Messaging.Outbox;

/// <summary>When the relay loop last finished a pass. The relay writes it, the health check reads it, on different threads.</summary>
public sealed class RelayHeartbeat
{
    private long _lastLoopAtTicks;

    /// <summary>Null until the relay's first loop.</summary>
    public DateTimeOffset? LastLoopAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastLoopAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Stamp(DateTimeOffset now) => Interlocked.Exchange(ref _lastLoopAtTicks, now.UtcTicks);
}
