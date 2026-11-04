using System.Collections;
using System.Diagnostics;

namespace NewStore.Catalog.Tests;

/// <summary>
/// Collects exported spans. The in-memory exporter adds on request threads while tests read, and
/// <see cref="List{T}"/> is not safe for that: Add raises the count before it writes the item, so a
/// reader can see a null. A lock around every access fixes it, and tests read a <see cref="Snapshot"/>.
/// </summary>
public sealed class SpanSink : ICollection<Activity>
{
    private readonly Lock _gate = new();
    private readonly List<Activity> _spans = [];

    public Activity[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _spans];
        }
    }

    public int Count
    {
        get { lock (_gate) { return _spans.Count; } }
    }

    public bool IsReadOnly => false;

    public void Add(Activity item)
    {
        lock (_gate)
        {
            _spans.Add(item);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _spans.Clear();
        }
    }

    public bool Contains(Activity item)
    {
        lock (_gate) { return _spans.Contains(item); }
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_gate) { _spans.CopyTo(array, arrayIndex); }
    }

    public bool Remove(Activity item)
    {
        lock (_gate) { return _spans.Remove(item); }
    }

    public IEnumerator<Activity> GetEnumerator() => ((IEnumerable<Activity>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}