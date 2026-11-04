# 0001. The readiness check has its own connection pool

## Status

Accepted

## Date

2026-11-04

## Context

The PostgreSQL readiness check first borrowed a connection from the pool that serves requests. When traffic fills that pool, the check waits for a free connection, its 3-second timeout fires and `/health/ready` answers Unhealthy. Kubernetes then takes a pod that is only busy out of rotation. Its load moves to pods that are just as busy, they fail readiness too, and the fleet empties out while the database is fine.

A readiness probe should fail only when the pod can't do its job at all.

## Decision

`AddPostgres(connectionString)` registers a keyed `NpgsqlDataSource` (key `health`) built from the same connection string with `MaxPoolSize = 2`, and the readiness check uses only that data source.

## Alternatives considered

- **Share the request pool.** Readiness also reports a full pool. It is simple, with no second pool, but under load it turns into a readiness cascade, as described above.
- **Share the pool and report Degraded when it is full.** Kubernetes treats Degraded as ready by default, so it avoids the cascade. But the check still queues behind requests, its timeout still fires and the signal depends on the pool's state. The separate pool is simpler to reason about.

## Consequences

- Readiness answers "is PostgreSQL reachable?". It stays green while requests queue for a connection.
- Pool exhaustion has to show elsewhere: the Npgsql metrics (`AddNpgsqlInstrumentation`) and alerts on them. Phase 4 builds the dashboards.
- Each pod holds up to 2 more connections to PostgreSQL, so count them against `max_connections` when sizing the fleet.
