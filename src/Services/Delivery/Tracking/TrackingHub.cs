using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using NewStore.Delivery.Shipments;
using NewStore.ServiceDefaults.Security;

namespace NewStore.Delivery.Tracking;

/// <summary>
/// The buyer's live view of a shipment, at <see cref="Path"/>. <see cref="Track"/> joins the shipment's group and returns the
/// snapshot; <see cref="CourierPositionHandler"/> sends each new position to the group as <see cref="PositionMethod"/>. A group is
/// kept in memory and not across a reconnect, so the client calls <see cref="Track"/> again after every one.
/// </summary>
[Authorize(Policies.SignedIn)]
public sealed class TrackingHub(ShipmentReader shipments) : Hub
{
    public const string Path = "/hubs/tracking";

    /// <summary>The client method that gets one new <see cref="CourierPosition"/>.</summary>
    public const string PositionMethod = "position";

    /// <summary>The group of the connections that track <paramref name="shipmentId"/>.</summary>
    public static string Group(Guid shipmentId) => $"shipment:{shipmentId}";

    /// <summary>
    /// Starts sending <paramref name="shipmentId"/>'s positions to this connection and returns the shipment as it is now. Someone
    /// else's shipment and a missing one both throw <c>not-found</c>, as the REST API answers 404 for both.
    /// </summary>
    public async Task<ShipmentDetails> Track(Guid shipmentId)
    {
        var buyerId = Context.User!.UserId();
        var ct = Context.ConnectionAborted;

        // The group is no security boundary: the check comes before the join.
        if (await shipments.GetAsync(shipmentId, buyerId, ct) is null)
        {
            throw new HubException("not-found");
        }

        // Join first, read second. A position stored in between then arrives twice, in the snapshot and as a message, and the
        // client drops the repeat by its seq. The other order would lose it.
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(shipmentId), ct);
        return await shipments.GetAsync(shipmentId, buyerId, ct) ?? throw new HubException("not-found");
    }

    /// <summary>Stops sending <paramref name="shipmentId"/>'s positions to this connection.</summary>
    public Task Untrack(Guid shipmentId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(shipmentId), Context.ConnectionAborted);
}
