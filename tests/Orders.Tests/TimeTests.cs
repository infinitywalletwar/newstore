namespace NewStore.Orders.Tests;

public class TimeTests
{
    [Fact]
    public void GetUtcNowToMicrosecond_cuts_the_sub_microsecond_part()
    {
        // The clock ends every time in 300 ns; the helper must cut it, as PostgreSQL's timestamptz would.
        var clock = new SubMicrosecondClock();
        Assert.Equal(3, clock.GetUtcNow().UtcTicks % 10);

        Assert.Equal(0, clock.GetUtcNowToMicrosecond().UtcTicks % 10);
    }
}
