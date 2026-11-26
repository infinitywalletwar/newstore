namespace NewStore.Delivery.Tracking;

public static class TrackingExtensions
{
    /// <summary>SignalR for <see cref="TrackingHub"/>. <see cref="CourierPositionHandler"/> sends through its hub context.</summary>
    public static IHostApplicationBuilder AddTracking(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSignalR();
        return builder;
    }
}
