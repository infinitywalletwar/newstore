using Microsoft.AspNetCore.SignalR;

namespace NewStore.Delivery.Tracking;

/// <summary>The buyer's live view of a shipment, at <see cref="Path"/>.</summary>
public sealed class TrackingHub : Hub
{
    public const string Path = "/hubs/tracking";

    /// <summary>The client method that gets one new <see cref="CourierPosition"/>.</summary>
    public const string PositionMethod = "position";

    /// <summary>The group of the connections that track <paramref name="shipmentId"/>.</summary>
    public static string Group(Guid shipmentId) => $"shipment:{shipmentId}";
}
