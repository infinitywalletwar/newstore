using Microsoft.Extensions.Caching.Distributed;

namespace NewStore.Catalog.Caching;

/// <summary>
/// Wraps the Redis <see cref="IDistributedCache"/> so that Redis being down costs a read at most one
/// timeout, once. A failed call is swallowed (reads return null, writes do nothing) and starts a
/// <see cref="Window"/> in which the inner cache is not called at all, so the next reads do not each wait
/// out the 1 s Redis timeout. The first failure in a minute is logged; the rest are not.
/// </summary>
internal sealed class TolerantDistributedCache(IDistributedCache inner, TimeProvider time, ILogger<TolerantDistributedCache> logger)
    : IDistributedCache, IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    // An async call that has not finished in this time counts as failed. Redis's own timeout is 1 s; this
    // keeps the first read after Redis dies well inside the 2 s budget even when the client is slow to notice.
    private static readonly TimeSpan CallBudget = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private long _skipUntilTicks;
    private long _nextLogTicks;

    public byte[]? Get(string key) => Run(() => inner.Get(key), null);

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        RunAsync(() => inner.GetAsync(key, token), null, token);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        Run(() => { inner.Set(key, value, options); return 0; }, 0);

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        RunAsync(async () => { await inner.SetAsync(key, value, options, token); return 0; }, 0, token);

    public void Refresh(string key) => Run(() => { inner.Refresh(key); return 0; }, 0);

    public Task RefreshAsync(string key, CancellationToken token = default) =>
        RunAsync(async () => { await inner.RefreshAsync(key, token); return 0; }, 0, token);

    public void Remove(string key) => Run(() => { inner.Remove(key); return 0; }, 0);

    public Task RemoveAsync(string key, CancellationToken token = default) =>
        RunAsync(async () => { await inner.RemoveAsync(key, token); return 0; }, 0, token);

    public void Dispose() => (inner as IDisposable)?.Dispose();

    private T Run<T>(Func<T> call, T fallback)
    {
        if (InWindow())
        {
            return fallback;
        }

        try
        {
            return call();
        }
        catch (Exception ex)
        {
            Failed(ex);
            return fallback;
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> call, T fallback, CancellationToken token)
    {
        if (InWindow())
        {
            return fallback;
        }

        try
        {
            return await call().WaitAsync(CallBudget, time, token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Failed(ex);
            return fallback;
        }
    }

    private bool InWindow() => time.GetUtcNow().UtcTicks < Interlocked.Read(ref _skipUntilTicks);

    private void Failed(Exception ex)
    {
        var now = time.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _skipUntilTicks, now + Window.Ticks);

        var nextLog = Interlocked.Read(ref _nextLogTicks);
        if (now >= nextLog && Interlocked.CompareExchange(ref _nextLogTicks, now + LogInterval.Ticks, nextLog) == nextLog)
        {
            CacheLog.RedisFailed(logger, (int)Window.TotalSeconds, ex);
        }
    }
}

internal static partial class CacheLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Redis failed; reads and writes skip it for {Seconds} s and use the in-process cache only.")]
    public static partial void RedisFailed(ILogger logger, int seconds, Exception exception);
}
