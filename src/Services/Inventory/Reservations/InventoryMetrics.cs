using System.Diagnostics.Metrics;

namespace NewStore.Inventory;

/// <summary>
/// The meter is named <c>NewStore.Inventory</c>, so <c>AddServiceDefaults</c> exports it without registration.
/// </summary>
public sealed class InventoryMetrics : IDisposable
{
    private readonly Meter _meter = new("NewStore.Inventory");
    private readonly Counter<long> _reservations;
    private readonly Histogram<int> _attempts;

    public InventoryMetrics()
    {
        _reservations = _meter.CreateCounter<long>("inventory.reservations", description: "Reserve calls by outcome.");
        _attempts = _meter.CreateHistogram<int>(
            "inventory.reserve.attempts", description: "Database round trips of one reserve; 1 means no retry.");
    }

    public void RecordReserve(ReserveOutcome outcome, int attempts)
    {
        _reservations.Add(1, new KeyValuePair<string, object?>("outcome", outcome.GetType().Name));
        _attempts.Record(attempts);
    }

    public void Dispose() => _meter.Dispose();
}
