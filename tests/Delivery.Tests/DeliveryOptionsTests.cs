using Microsoft.Extensions.Options;

namespace NewStore.Delivery.Tests;

public sealed class DeliveryOptionsTests
{
    /// <summary>
    /// Each case is one bad setting. The host checks the options when it starts (<c>ValidateOnStart</c>) and throws, so the test needs
    /// no database or broker: the addresses below point at a closed port. Without a check the host goes on to wait for that broker, so
    /// the timeout turns a missing check into a failure instead of a hang.
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
