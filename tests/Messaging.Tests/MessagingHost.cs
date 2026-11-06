using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewStore.Messaging.Tests;

/// <summary>Builds a host the way a service's <c>Program.cs</c> would: a database context, then <c>AddMessaging</c>.</summary>
internal static class MessagingHost
{
    public static HostApplicationBuilder CreateBuilder(
        string bootstrapServers,
        string connectionString,
        string clientId,
        Action<MessagingBuilder>? configure = null,
        Action<MessagingOptions>? options = null,
        Action<IServiceCollection>? services = null)
    {
        // An empty builder: no appsettings or environment variables from the machine that runs the tests.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = clientId });
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["Messaging:BootstrapServers"] = bootstrapServers;

        builder.Services.AddDbContext<ThingsDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        builder.AddMessaging<ThingsDbContext>(configure ?? (_ => { }));

        // Fast loops, so tests wait for conditions, not for the production intervals.
        builder.Services.Configure<MessagingOptions>(o =>
        {
            o.RelayInterval = TimeSpan.FromMilliseconds(100);
            options?.Invoke(o);
        });

        services?.Invoke(builder.Services);
        return builder;
    }
}
