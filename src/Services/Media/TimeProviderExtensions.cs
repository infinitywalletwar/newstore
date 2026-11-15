namespace NewStore.Media;

internal static class TimeProviderExtensions
{
    /// <summary>
    /// The current UTC time cut to whole microseconds. PostgreSQL's <c>timestamp with time zone</c> has a resolution of
    /// 1 microsecond (https://www.postgresql.org/docs/current/datatype-datetime.html), and a .NET tick is 100 ns. A time that
    /// Media stores or returns comes from here, so the value in a response equals the value any later read returns.
    /// </summary>
    public static DateTimeOffset GetUtcNowToMicrosecond(this TimeProvider time)
    {
        var now = time.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);
    }
}
