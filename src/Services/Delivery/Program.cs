using Microsoft.Extensions.DependencyInjection.Extensions;

using NewStore.Contracts.Orders;
using NewStore.Delivery;
using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;
using NewStore.Messaging;
using NewStore.ServiceDefaults;
using NewStore.ServiceDefaults.Data;
using NewStore.ServiceDefaults.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDatabase<DeliveryDbContext>("Delivery");
var role = builder.AddDeliveryOptions();   // binds Delivery:Warehouse, :Courier and :Tracking in every role; returns Delivery:Role
builder.Services.TryAddSingleton(TimeProvider.System);
var api = role is DeliveryRole.All or DeliveryRole.Api;

// Delivery consumes Orders' ShipmentRequested through the inbox. The positions are a stream (ADR 0022): the courier produces them,
// the API role consumes them.
builder.AddMessaging<DeliveryDbContext>(m =>
{
    m.AddTopic(OrderEvents.Topic, OrderEvents.Partitions);
    m.AddTopic(CourierPosition.Topic, CourierPosition.Partitions);
    if (api)
    {
        m.AddHandler<ShipmentRequested, ShipmentRequestedHandler>(OrderEvents.Topic);
        m.AddIgnoredType<RefundRequested>(OrderEvents.Topic);   // Payments' event on the same topic
        // Task 5: if (builder.Configuration.GetValue("Delivery:Tracking:ConsumePositions", true))
        //             m.AddStreamHandler<CourierPosition, CourierPositionHandler>(CourierPosition.Topic);
    }
});

if (api)
{
    // Buyers' tokens are validated here (the gateway signs them in). Delivery calls no service, so it has no client of its own.
    builder.AddNewStoreAuth();
    builder.AddShipments();                  // ShipmentReader
    // Task 5: builder.AddTracking();        // SignalR, WebSockets only, the Redis backplane when ConnectionStrings:Redis is set
}

// Task 4: builder.AddCourier(runCourier: role is DeliveryRole.All or DeliveryRole.Courier);

var app = builder.Build();

// "migrate" runs as a one-shot command (ADR 0005); with no arguments the service runs. Delivery has no seed.
if (await app.RunDatabaseCommandsAsync<DeliveryDbContext>(args) is { } exitCode)
{
    return exitCode;
}

app.MapDefaultEndpoints();
if (api)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapShipmentEndpoints();
    // Task 5: app.MapTrackingHub();
}

app.Run();
return 0;
