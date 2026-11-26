using Microsoft.AspNetCore.Http.Connections;

using StackExchange.Redis;

namespace NewStore.Delivery.Tracking;

public static partial class TrackingExtensions
{
    /// <summary>
    /// SignalR for <see cref="TrackingHub"/>; <see cref="CourierPositionHandler"/> sends through its hub context. With
    /// <c>ConnectionStrings:Redis</c> set, every send goes through Redis to all instances (the backplane), so a position consumed by
    /// one instance reaches a browser connected to another. Without it each instance reaches only its own connections, which is
    /// enough for one instance, and the start logs a Warning.
    /// </summary>
    public static IHostApplicationBuilder AddTracking(this IHostApplicationBuilder builder)
    {
        var signalR = builder.Services.AddSignalR();
        if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            signalR.AddStackExchangeRedis(options =>
            {
                options.Configuration = ConfigurationOptions.Parse(redis);
                // Start, and keep retrying, while Redis is down. Meanwhile sends fail (the next Track's snapshot covers them), and so
                // do new connections: each subscribes to its channels in Redis, and the server closes it when that fails (the test
                // While_redis_is_down_a_new_connection_is_closed_and_track_fails). The page retries until Redis is back.
                options.Configuration.AbortOnConnectFail = false;
                // One Redis may serve several SignalR apps; the prefix keeps their channels apart.
                options.Configuration.ChannelPrefix = RedisChannel.Literal("newstore-delivery");
            });
        }
        else
        {
            builder.Services.AddHostedService<NoBackplaneWarning>();
        }

        return builder;
    }

    /// <summary>
    /// Maps <see cref="TrackingHub"/> at <see cref="TrackingHub.Path"/>, WebSockets only: a client that asks for another transport
    /// is refused, so no connection ever needs the sticky sessions that negotiate plus a fallback transport would (ADR 0023). The
    /// connection closes when the token it was opened with expires, and the client reconnects with a fresh one. The token comes in
    /// the upgrade's <c>Authorization</c> header, which the gateway sets, so the hub reads no <c>access_token</c> query string.
    /// </summary>
    public static IEndpointRouteBuilder MapTrackingHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<TrackingHub>(TrackingHub.Path, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.CloseOnAuthenticationExpiration = true;
        });
        return endpoints;
    }

    /// <summary>Logs once, when the host starts, that this instance has no backplane.</summary>
    private sealed partial class NoBackplaneWarning(ILogger<NoBackplaneWarning> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            LogNoBackplane(logger);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Warning, Message = "no Redis: SignalR runs without a backplane")]
        private static partial void LogNoBackplane(ILogger logger);
    }
}
