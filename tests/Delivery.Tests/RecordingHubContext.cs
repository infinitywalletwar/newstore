using System.Collections.Concurrent;

using Microsoft.AspNetCore.SignalR;

using NewStore.Delivery.Tracking;

namespace NewStore.Delivery.Tests;

/// <summary>
/// Stands in for <see cref="IHubContext{THub}"/> of <see cref="TrackingHub"/>: it records every message sent to a group instead of
/// sending it. With <see cref="Fail"/> set, every send throws, as one would while Redis is down.
/// </summary>
internal sealed class RecordingHubContext : IHubContext<TrackingHub>
{
    private readonly ConcurrentQueue<SentMessage> _sent = new();

    /// <summary>Every send, in the order the handler made them.</summary>
    public IReadOnlyList<SentMessage> Sent => [.. _sent];

    public bool Fail { get; set; }

    public IHubClients Clients => new RecordingClients(this);

    public IGroupManager Groups => throw new NotSupportedException("The handler only sends.");

    /// <summary>The positions sent to <paramref name="shipmentId"/>'s group.</summary>
    public IReadOnlyList<CourierPosition> SentTo(Guid shipmentId) =>
        [.. Sent.Where(s => s.Group == TrackingHub.Group(shipmentId)).Select(s => (CourierPosition)s.Args.Single()!)];

    private sealed class RecordingClients(RecordingHubContext context) : IHubClients
    {
        public IClientProxy Group(string groupName) => new RecordingProxy(context, groupName);

        public IClientProxy All => throw Unexpected();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw Unexpected();

        public ISingleClientProxy Client(string connectionId) => throw Unexpected();

        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => throw Unexpected();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw Unexpected();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw Unexpected();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw Unexpected();

        public IClientProxy User(string userId) => throw Unexpected();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw Unexpected();

        private static NotSupportedException Unexpected() => new("The handler sends to a shipment's group only.");
    }

    private sealed class RecordingProxy(RecordingHubContext context, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (context.Fail)
            {
                throw new InvalidOperationException("The send fails on purpose.");
            }

            context._sent.Enqueue(new SentMessage(group, method, args));
            return Task.CompletedTask;
        }
    }
}

internal sealed record SentMessage(string Group, string Method, object?[] Args);
