using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using NewStore.Delivery.Tracking;
using NewStore.Messaging;

namespace NewStore.Delivery.Courier;

/// <summary>
/// One courier: the trips it holds and what it has sent for each. <see cref="CourierService"/> calls <see cref="TickAsync"/> every
/// <see cref="CourierOptions.TickInterval"/>; a test makes a second runner with <c>ActivatorUtilities</c> and ticks it by hand.
/// A singleton, so each tick gets a scope of its own for the scoped <see cref="CourierLeases"/> and its database context.
/// <para>
/// What it keeps in memory is only an optimisation: the last seq it sent per trip, so it doesn't send one seq twice. Everything a
/// trip needs is in the database (its start, its ends) or computed from it (<see cref="CourierTimeline"/>), so a courier that dies
/// loses nothing another courier can't rebuild.
/// </para>
/// </summary>
public sealed partial class CourierRunner(
    IServiceScopeFactory scopes,
    IStreamProducer producer,
    TimeProvider time,
    IOptions<CourierOptions> options,
    ILogger<CourierRunner> logger)
{
    private readonly ConcurrentDictionary<Guid, Trip> _held = new();
    private DateTimeOffset _renewedAt = DateTimeOffset.MinValue;

    /// <summary>This courier in <c>courier_locked_by</c>: the host name (in a container, its id) and 8 random hex characters.</summary>
    public string CourierId { get; } = $"{Environment.MachineName}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    /// <summary>The shipments this courier holds now.</summary>
    public IReadOnlyCollection<Guid> Held => [.. _held.Keys];

    private CourierOptions Options => options.Value;

    /// <summary>
    /// Renew, claim, send each held trip's point for now, finish what was delivered. A failure is logged and the next tick starts
    /// over: a lease that wasn't renewed is renewed next time, and a seq that wasn't sent is replaced by the next one.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var leases = scope.ServiceProvider.GetRequiredService<CourierLeases>();
            var now = time.GetUtcNow();

            await RenewAsync(leases, now, ct);
            await ClaimAsync(leases, ct);
            await ProduceAsync(now, ct);
            await FinishAsync(leases, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TickFailed(logger, CourierId, ex);
        }
    }

    /// <summary>Ends this courier's leases now, so another courier claims its trips on its next tick. For a graceful stop.</summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CourierLeases>().ReleaseAsync(CourierId, ct);
        Released(logger, CourierId, _held.Count);
        _held.Clear();
    }

    /// <summary>Step 1: once per <see cref="CourierOptions.LeaseRenewInterval"/>; a trip the renewal doesn't return is no longer ours.</summary>
    private async Task RenewAsync(CourierLeases leases, DateTimeOffset now, CancellationToken ct)
    {
        if (_held.IsEmpty || now - _renewedAt < Options.LeaseRenewInterval)
        {
            return;
        }

        var stillHeld = (await leases.RenewAsync(CourierId, [.. _held.Keys], ct)).ToHashSet();
        foreach (var id in _held.Keys.Where(id => !stillHeld.Contains(id)))
        {
            _held.TryRemove(id, out _);
            LeaseLost(logger, CourierId, id);
        }

        _renewedAt = now;
    }

    /// <summary>Step 2: up to <see cref="CourierOptions.ClaimsPerTick"/> new trips, while under <see cref="CourierOptions.MaxShipments"/>.</summary>
    private async Task ClaimAsync(CourierLeases leases, CancellationToken ct)
    {
        for (var i = 0; i < Options.ClaimsPerTick && _held.Count < Options.MaxShipments; i++)
        {
            if (await leases.ClaimAsync(CourierId, ct) is not { } claim)
            {
                return;
            }

            // A trip this courier already holds comes back when its own lease ran out before a renewal and nobody else took it:
            // it keeps the seq already sent.
            _held.GetOrAdd(claim.Id, _ => new Trip(claim));
            Claimed(logger, CourierId, claim.Id, claim.StartedAt);
        }
    }

    /// <summary>
    /// Step 3: each held trip's point for <paramref name="now"/>, when its seq is above the last one sent, all at once. A trip whose
    /// message isn't acknowledged keeps its last seq, so the next tick sends that trip's point for its own now.
    /// </summary>
    private Task ProduceAsync(DateTimeOffset now, CancellationToken ct) =>
        Task.WhenAll(_held.Values
            .Select(trip => (Trip: trip, Seq: CourierTimeline.SeqAt(now, trip.Shipment.StartedAt, Options)))
            .Where(due => due.Seq > due.Trip.SentSeq)
            .Select(due => ProduceAsync(due.Trip, due.Seq, ct)));

    private async Task ProduceAsync(Trip trip, int seq, CancellationToken ct)
    {
        var shipment = trip.Shipment;
        var point = CourierTimeline.At(seq, shipment.StartedAt, shipment.From, shipment.To, Options);
        var position = new CourierPosition
        {
            ShipmentId = shipment.Id,
            Seq = point.Seq,
            Status = point.Status.ToString(),
            Latitude = point.Position.Latitude,
            Longitude = point.Position.Longitude,
            RecordedAt = point.RecordedAt,
        };

        try
        {
            // Completes only when Kafka acknowledged the message (IStreamProducer: idempotent, acks=all).
            await producer.ProduceAsync(CourierPosition.Topic, shipment.Id.ToString(), position, ct);
            trip.SentSeq = seq;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ProduceFailed(logger, shipment.Id, seq, ex);
        }
    }

    /// <summary>
    /// Step 4: a trip whose last seq Kafka acknowledged is finished and leaves <see cref="Held"/>. A crash between the
    /// acknowledgement and this write leaves the trip open; the courier that takes it over sends the same last seq again, which the
    /// consumer ignores, and finishes it.
    /// </summary>
    private async Task FinishAsync(CourierLeases leases, CancellationToken ct)
    {
        var lastSeq = CourierTimeline.LastSeq(Options);
        foreach (var trip in _held.Values.Where(t => t.SentSeq == lastSeq).ToList())
        {
            if (await leases.FinishAsync(trip.Shipment.Id, CourierId, ct))
            {
                Finished(logger, CourierId, trip.Shipment.Id);
            }
            else
            {
                LeaseLost(logger, CourierId, trip.Shipment.Id);
            }

            _held.TryRemove(trip.Shipment.Id, out _);
        }
    }

    /// <summary>A held trip and the last seq Kafka acknowledged for it from this courier (-1 before the first).</summary>
    private sealed class Trip(ClaimedShipment shipment)
    {
        public ClaimedShipment Shipment { get; } = shipment;

        public int SentSeq { get; set; } = -1;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Courier {CourierId} claimed shipment {ShipmentId}, started at {StartedAt:O}.")]
    private static partial void Claimed(ILogger logger, string courierId, Guid shipmentId, DateTimeOffset startedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Courier {CourierId} delivered shipment {ShipmentId}.")]
    private static partial void Finished(ILogger logger, string courierId, Guid shipmentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Courier {CourierId} lost shipment {ShipmentId} to another courier.")]
    private static partial void LeaseLost(ILogger logger, string courierId, Guid shipmentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Position {Seq} of shipment {ShipmentId} was not acknowledged; a later tick sends a newer one.")]
    private static partial void ProduceFailed(ILogger logger, Guid shipmentId, int seq, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Courier {CourierId}'s tick failed; the next tick starts over.")]
    private static partial void TickFailed(ILogger logger, string courierId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Courier {CourierId} released its {Count} shipments.")]
    private static partial void Released(ILogger logger, string courierId, int count);
}
