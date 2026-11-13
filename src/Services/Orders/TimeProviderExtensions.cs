namespace NewStore.Orders;

internal static class TimeProviderExtensions
{
    /// <summary>
    /// The current UTC time cut to whole microseconds. PostgreSQL's <c>timestamp with time zone</c> has a resolution of
    /// 1 microsecond (https://www.postgresql.org/docs/current/datatype-datetime.html), and a .NET tick is 100 ns. A time that
    /// Orders stores or returns comes from here, so a response shows the value any later read returns. The same arithmetic as
    /// Inventory's helper; it is not shared because a two-line function is cheaper than a shared project.
    /// </summary>
    public static DateTimeOffset GetUtcNowToMicrosecond(this TimeProvider time)
    {
        var now = time.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);
    }
}
