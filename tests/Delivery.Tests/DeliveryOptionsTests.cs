using Microsoft.Extensions.Options;

namespace NewStore.Delivery.Tests;

public sealed class DeliveryOptionsTests
{
    /// <summary>
    /// Each case is one bad setting, and the start fails with a message that names it. The options are checked when the host starts
    /// (<c>ValidateOnStart</c>), and <c>Delivery:Role</c> before the host is built, so the test needs no database or broker: the
    /// addresses below point at a closed port. With a check missing, the host would go on to start, and <c>TopicCreator</c> would
    /// spend its five attempts on that port (one such run took minutes), so the timeout turns a missing check into a quick failure.
    /// <para>
    /// The librdkafka "Connection refused" lines on stderr are expected. <c>KafkaProducer</c> is built when the host resolves its
    /// hosted services, before the validator throws, and the factory never disposes that provider, so the producer keeps retrying
    /// until the test process ends. A producer that connects lazily is a Messaging follow-up, not part of Delivery.
    /// </para>
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData("Delivery:Role", "Delivery:Role=5")]
    [InlineData("Delivery:Warehouse:Name", "Delivery:Warehouse:Name=   ")]
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

        // An OptionsValidationException for the options, an InvalidOperationException for the role.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => _ = app.Services, TestContext.Current.CancellationToken));

        Assert.True(error is OptionsValidationException or InvalidOperationException, error.ToString());
        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }
}
