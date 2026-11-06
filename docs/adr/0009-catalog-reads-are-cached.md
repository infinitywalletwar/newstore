# 0009. Catalog reads are cached with HybridCache; Redis is a degraded dependency

## Status

Accepted

## Date

2026-11-06

## Context

The video page (`GET /videos/{id}`) and the channel page (`GET /channels/{slug}`) are the hot reads of V-4. Many viewers ask for the same few rows, and the rows change rarely. Search is different: its inputs are free text, so almost no two requests are alike.

Two questions follow. Which cache? And what does Catalog do when the cache is down?

## Decision

- **HybridCache** with Redis as its secondary cache. The primary cache is in the process, the secondary one is shared between instances. Entries live 60 seconds in both. Search is not cached.
- **Keys:** `video:{id}` and `channel:{slug}:{page}:{pageSize}`. **Tags:** `video:{id}` and `channel:{slug}`. A later task that changes a channel removes the tag of the channel page and the tags of its videos.
- **Redis is a degraded dependency.** The readiness check `redis` pings Redis on its own connection with a 3 second budget and reports `Degraded` on failure, not `Unhealthy`: `/health/ready` answers 200 with the body `Degraded`, so the orchestrator keeps Catalog in rotation. Reads go to PostgreSQL.
- **`TolerantDistributedCache`** wraps the Redis `IDistributedCache`. A failed or slow (over 500 ms) call is swallowed, starts a 30 second window in which Redis is not called, and is logged at most once a minute.
- Without `ConnectionStrings:Redis` Catalog runs with the in-process cache only, and has no `redis` check.

Staleness of up to 60 seconds is fine for V-4: a title or a description that changes shows up a minute late. E-3 says view and like counts may lag, and these pages do not show counts.

## Alternatives considered

- **`IMemoryCache` alone.** One cache per instance, nothing to run. It has no protection when many requests ask for the same missing key: each runs the query. HybridCache has that protection in the box ([HybridCache in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid): "only one concurrent caller for a given key calls the factory method"), and it also gives a cache shared by instances once Redis is added.
- **A bare `IDistributedCache`.** It stores bytes, so the code serializes and deserializes, and every read pays a network round trip. The same page notes that neither `IMemoryCache` nor `IDistributedCache` has tags, so removing a group of entries needs our own bookkeeping. HybridCache adds the in-process first level, serialization, stampede protection and tags on top of an `IDistributedCache`.
- **Redis as `Unhealthy`.** Then a Redis outage would make every Catalog instance unready and take the reads down with it, although PostgreSQL can serve them. Degraded says "working, with less help", which is the truth.

## Consequences

- **Stampede protection.** For one key, one caller per process runs the query; the others wait for it. It does not extend to other instances, so N instances still send N queries on a cold key.
- **The tag is not the channel id.** HybridCache takes the tags before the factory runs, and the channel of a video is known only after the row is read. So the video entry cannot carry `channel:{channelId}` without an extra query, which would defeat the cache. A writer drops a channel by removing the tag of its page and the tag of each of its videos (it knows their ids). Tag removal is a logical rule, not a deletion, and does not reach the in-process caches of other instances, so those still serve the old entry until the 60 seconds pass ([same page](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)).
- **A missing id is not cached.** `GetOrCreateAsync` caches the null result; the endpoints remove it right away, so a video or channel created later is found at once. The price is one cache call per unknown id.
- **Redis down costs one slow read.** Measured in this task, with HybridCache and the StackExchange.Redis client at 1 second timeouts and no wrapper: the first read of an uncached video took 1,030 ms in two runs, and HybridCache logged the Redis exceptions and returned the value. A wrapper without the 500 ms call budget took 1,066 to 1,079 ms in three runs and 2,071 ms in a fourth. With the budget the first read took 559 to 582 ms in five runs, and the next reads do not wait. The cost is that Redis returns to use up to 30 seconds after it recovers.
- **Traces.** HybridCache runs the factory without the caller's `Activity.Current` (measured: it is null inside the factory), so the database spans of a miss started new traces. The endpoints pass the activity in and restore it in the factory.
