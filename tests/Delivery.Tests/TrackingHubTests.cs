using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;

namespace NewStore.Delivery.Tests;

/// <summary>
/// The hub on one host. Positions are stored by calling the handler, not through Kafka, so each test decides exactly when one
/// arrives (the fixture consumes no stream).
/// </summary>
public sealed class TrackingHubTests(NoPositionsFixture delivery) : IClassFixture<NoPositionsFixture>
{
    private static readonly TimeSpan s_quiet = TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTimeOffset Start => Api.Micro(delivery.Clock.GetUtcNow());

    private static string BuyerToken(Guid buyer, TimeSpan? lifetime = null) => TestTokens.User(buyer, "delivery", lifetime: lifetime);

    [Fact]
    public async Task Track_returns_the_snapshot_then_streams_new_positions()
    {
        var buyer = Guid.NewGuid();
        var shipment = await delivery.CreateShipmentAsync(buyer);
        var start = Start;
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, start, ShipmentStatus.Preparing), Positions.At(shipment.Id, 1, start, ShipmentStatus.Preparing));
        await using var connection = await HubClient.ConnectAsync(delivery, BuyerToken(buyer));
        var positions = connection.Listen<CourierPosition>(TrackingHub.PositionMethod);

        var snapshot = await connection.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct);
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 2, start, ShipmentStatus.PickedUp));

        Assert.Equal(shipment.Id, snapshot.Id);
        Assert.Equal([0, 1], snapshot.Path.Select(p => p.Seq));
        Assert.Equal(Positions.At(shipment.Id, 2, start, ShipmentStatus.PickedUp), await positions.NextAsync());
    }

    [Fact]
    public async Task Stranger_and_missing_shipment_both_get_not_found()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var missing = Guid.CreateVersion7();
        await using var stranger = await HubClient.ConnectAsync(delivery, BuyerToken(Guid.NewGuid()));
        var positions = stranger.Listen<CourierPosition>(TrackingHub.PositionMethod);

        var someoneElses = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct));
        var notThere = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync<ShipmentDetails>("Track", missing, Ct));

        // SignalR passes a HubException's message to the caller behind its own prefix, and hides any other exception's message.
        Assert.Equal("An unexpected error occurred invoking 'Track' on the server. HubException: not-found", someoneElses.Message);
        Assert.Equal(someoneElses.Message, notThere.Message);

        // The connection joined no group: positions for either shipment don't reach it.
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, Start));
        await delivery.Services.GetRequiredService<IHubContext<TrackingHub>>().Clients.Group(TrackingHub.Group(missing))
            .SendAsync(TrackingHub.PositionMethod, Positions.At(missing, 0, Start), Ct);
        await positions.AssertNoneAsync(s_quiet);
    }

    [Fact]
    public async Task Hub_refuses_a_connection_without_a_user_token()
    {
        var anonymous = await Assert.ThrowsAsync<InvalidOperationException>(() => HubClient.ConnectAsync(delivery, null));
        var service = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HubClient.ConnectAsync(delivery, TestTokens.Client("orders", "delivery")));

        // The test server's WebSocket client reports the status of a handshake that did not upgrade.
        Assert.Equal("Incomplete handshake, status code: 401", anonymous.Message);
        Assert.Equal("Incomplete handshake, status code: 403", service.Message);
    }

    [Fact]
    public async Task Untrack_stops_the_messages()
    {
        var buyer = Guid.NewGuid();
        var shipment = await delivery.CreateShipmentAsync(buyer);
        var start = Start;
        await using var connection = await HubClient.ConnectAsync(delivery, BuyerToken(buyer));
        var positions = connection.Listen<CourierPosition>(TrackingHub.PositionMethod);
        await connection.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct);
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, start));
        Assert.Equal(0, (await positions.NextAsync()).Seq);

        await connection.InvokeAsync("Untrack", shipment.Id, Ct);
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 1, start));

        await positions.AssertNoneAsync(s_quiet);
    }

    [Fact]
    public async Task Reconnected_client_tracks_again_and_the_snapshot_has_what_it_missed()
    {
        // Review Focus 1: the page loses its connection mid-trip. Positions sent meanwhile reach no one, and the new connection is
        // in no group, so the page calls Track again and the snapshot fills the gap.
        var buyer = Guid.NewGuid();
        var shipment = await delivery.CreateShipmentAsync(buyer);
        var start = Start;
        await Positions.HandleAsync(delivery, [.. Enumerable.Range(0, 3).Select(seq => Positions.At(shipment.Id, seq, start))]);
        var first = await HubClient.ConnectAsync(delivery, BuyerToken(buyer));
        await first.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct);
        await first.DisposeAsync();

        await Positions.HandleAsync(delivery, [.. Enumerable.Range(3, 3).Select(seq => Positions.At(shipment.Id, seq, start))]);
        await using var second = await HubClient.ConnectAsync(delivery, BuyerToken(buyer));
        var snapshot = await second.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct);

        Assert.Equal([0, 1, 2, 3, 4, 5], snapshot.Path.Select(p => p.Seq));
        Assert.Equal(5, snapshot.LastPosition!.Seq);
    }

    [Fact]
    public async Task Connection_closes_when_the_token_expires()
    {
        // Review Focus 1. JwtBearer sets the ticket's ExpiresUtc to the token's own expiry, without the validation's ClockSkew, and
        // CloseOnAuthenticationExpiration closes the connection once that time has passed. So a 5-second token closes the
        // connection about 5 seconds in, although the 5-minute ClockSkew would still accept the same token on a new handshake.
        var buyer = Guid.NewGuid();
        var lifetime = TimeSpan.FromSeconds(5);
        var issued = Stopwatch.StartNew();
        await using var connection = await HubClient.ConnectAsync(delivery, BuyerToken(buyer, lifetime));
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
        // Not earlier than the token's expiry (a second's slack for the token's whole-second exp claim).
        Assert.True(issued.Elapsed >= lifetime - TimeSpan.FromSeconds(1), $"Closed after {issued.Elapsed}, before the token expired.");
    }

    [Fact]
    public async Task Position_message_has_camel_case_fields()
    {
        var buyer = Guid.NewGuid();
        var shipment = await delivery.CreateShipmentAsync(buyer);
        await using var connection = await HubClient.ConnectAsync(delivery, BuyerToken(buyer));
        var raw = connection.Listen<JsonElement>(TrackingHub.PositionMethod);
        await connection.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct);

        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, Start, ShipmentStatus.Preparing));

        // The web app's PositionMessage (Task 6) reads exactly these names.
        var message = await raw.NextAsync();
        Assert.Equal(
            ["shipmentId", "seq", "status", "latitude", "longitude", "recordedAt"],
            message.EnumerateObject().Select(p => p.Name));
        Assert.Equal(shipment.Id, message.GetProperty("shipmentId").GetGuid());
        Assert.Equal("Preparing", message.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Server_refuses_long_polling()
    {
        // The browser is told to use WebSockets only; the server enforces it, so no client can fall back to a transport that
        // needs sticky sessions behind the gateway's round robin.
        var token = BuyerToken(Guid.NewGuid());
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(delivery.Server.BaseAddress, "hubs/tracking"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => delivery.Server.CreateHandler();
                options.Headers["Authorization"] = new AuthenticationHeaderValue("Bearer", token).ToString();
            })
            .Build();

        var refused = await Assert.ThrowsAsync<AggregateException>(() => connection.StartAsync(Ct));

        // The negotiate answer offered WebSockets alone, which this client has turned off.
        Assert.Equal(
            "Unable to connect to the server with any of the available transports. (WebSockets failed: The transport is disabled by the client.)",
            refused.Message);
    }
}
