using System.Threading.Channels;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace NewStore.Delivery.Tests;

/// <summary>
/// A SignalR client of the tracking hub, set up as the browser's: WebSockets only and no negotiate request. The socket goes through
/// the test server, which has no network, and the token travels in the <c>Authorization</c> header of the upgrade, as the gateway
/// sends it.
/// </summary>
internal static class HubClient
{
    public static async Task<HubConnection> ConnectAsync(DeliveryFixture app, string? token)
    {
        var connection = Build(app, token);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>The connection, not started yet.</summary>
    public static HubConnection Build(DeliveryFixture app, string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "hubs/tracking"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = app.Server.CreateWebSocketClient();
                    if (token is not null)
                    {
                        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
                    }

                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();

    /// <summary>Collects what the server sends to <paramref name="method"/> from now on.</summary>
    public static Inbox<T> Listen<T>(this HubConnection connection, string method)
    {
        var inbox = new Inbox<T>();
        connection.On<T>(method, inbox.Add);
        return inbox;
    }
}

/// <summary>The messages of one client method, in the order they arrived.</summary>
internal sealed class Inbox<T>
{
    private readonly Channel<T> _messages = Channel.CreateUnbounded<T>();

    public void Add(T message) => _messages.Writer.TryWrite(message);

    /// <summary>The next message; fails the test when none arrives within <paramref name="timeout"/> (10 s by default).</summary>
    public async Task<T> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            return await _messages.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"No message arrived within {timeout ?? TimeSpan.FromSeconds(10)}.");
            throw;
        }
    }

    /// <summary>Waits <paramref name="wait"/> and fails the test if a message arrived by then.</summary>
    public async Task AssertNoneAsync(TimeSpan wait)
    {
        await Task.Delay(wait, TestContext.Current.CancellationToken);
        Assert.False(_messages.Reader.TryRead(out var message), $"Expected no message, got {message}.");
    }
}
