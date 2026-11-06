using System.Diagnostics;

namespace NewStore.Catalog.Caching;

/// <summary>
/// HybridCache runs the factory without the caller's <see cref="Activity.Current"/> (measured: it is null
/// inside the factory), so the database spans of a cache miss would start new, unconnected traces. The
/// endpoint passes <see cref="Activity.Current"/> in as state, and the factory calls <see cref="Restore"/>.
/// </summary>
internal static class CacheTrace
{
    public static void Restore(Activity? caller) => Activity.Current = caller;
}
