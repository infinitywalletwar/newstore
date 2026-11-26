using Microsoft.Extensions.Options;

namespace NewStore.Delivery.Tests;

public sealed class DeliveryOptionsTests
{
    /// <summary>
    /// Each case is one bad setting. The options are checked when the host starts (<c>ValidateOnStart</c>), before any hosted service
    /// runs, so the app needs no reachable database or broker: the addresses below are never used. Without the check the host would
    /// go on to start and wait for the broker, so the timeout turns that into a failure instead of a hang.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData("Delivery:Warehouse:Latitude", "Delivery:Warehouse:Latitude=91")]
    [InlineData("Delivery:Courier:TickInterval", "Delivery:Courier:TickInterval=00:00:00")]
    [InlineData("Delivery:Courier:Lease", "Delivery:Courier:Lease=00:00:20", "Delivery:Courier:LeaseRenewInterval=00:00:10")]
    public async Task Invalid_settings_fail_startup(string named, params string[] settings)
    {
        using var app = new NewStoreAppFactory<Program>();
        app.Settings["ConnectionStrings:Delivery"] = "Host=127.0.0.1;Port=1;Database=unused";
        app.Settings["Messaging:BootstrapServers"] = "127.0.0.1:1";
        foreach (var setting in settings)
        {
            var (key, value) = (setting[..setting.IndexOf('=')], setting[(setting.IndexOf('=') + 1)..]);
            app.Settings[key] = value;
        }

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => Task.Run(() => _ = app.Services, TestContext.Current.CancellationToken));

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }
}
