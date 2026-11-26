using Microsoft.AspNetCore.Http.Connections;

namespace NewStore.Delivery.Tracking;

public static class TrackingExtensions
{
    /// <summary>SignalR for <see cref="TrackingHub"/>. <see cref="CourierPositionHandler"/> sends through its hub context.</summary>
    public static IHostApplicationBuilder AddTracking(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSignalR();
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
}
