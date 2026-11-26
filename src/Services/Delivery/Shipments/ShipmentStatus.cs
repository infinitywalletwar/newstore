namespace NewStore.Delivery.Shipments;

/// <summary>Where a parcel is. Stored and sent as the name. The courier (Task 4) moves a shipment along these in order.</summary>
public enum ShipmentStatus { Preparing, PickedUp, InTransit, Delivered }
