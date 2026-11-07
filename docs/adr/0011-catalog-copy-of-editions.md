# Catalog's copy of editions comes from snapshots

## Status

Accepted

## Date

2026-11-07

## Context

Story E-3: a viewer sees each edition's price and how many copies are left, and "the number left may lag behind real sales by a few seconds". Inventory owns editions. The README gives Catalog "a read copy of editions", so the video page never waits on Inventory.

`copies_left` changes on every reserve, release, expiry and edit. In the k6 run, 1,000 buyers hit one edition at once. Catalog needs only the latest number, not each step.

## Decision

1. **Inventory publishes snapshots, not one event per change.** An `EditionSnapshotPublisher` loop runs once a second. Each pass finds the editions whose public state (price, copies total, copies left, ended at) differs from the last snapshot it sent. It enqueues one `EditionSnapshot` per such edition through the outbox and records what it sent, all in one transaction. Many reserves between two passes make one snapshot. This is Fowler's "Event-Carried State Transfer": the receiver keeps its own copy, so it doesn't "need to contact the source system in order to do further work" ([Fowler, "What do you mean by Event-Driven?"](https://martinfowler.com/articles/201701-event-driven.html)).
2. **The last sent state lives in its own table, `edition_snapshots`** (edition id, version, the four values, published at). The pass reads `editions` with a plain `SELECT` and writes only `edition_snapshots`, so it never takes a lock on an edition row that a reserve needs.
3. **One pass at a time.** The pass starts with `pg_try_advisory_xact_lock`, which will "either obtain the lock immediately and return `true`, or return `false` without waiting", and the lock is "held until the current transaction ends" ([PostgreSQL, Advisory Lock Functions](https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADVISORY-LOCKS)). With two Inventory instances, the second one skips the tick.
4. **Every snapshot carries a version, and Catalog keeps only a newer one.** The version is 1 for an edition's first snapshot, then the previous version plus one. Catalog's handler upserts with `INSERT … ON CONFLICT (id) DO UPDATE … WHERE editions.version < excluded.version`: "Only rows for which this expression returns `true` will be updated" ([PostgreSQL, INSERT](https://www.postgresql.org/docs/current/sql-insert.html)).
5. **The event type lives in `NewStore.Contracts`** (`src/Shared/Contracts/Inventory/EditionSnapshot.cs`), which both services reference. A consumer picks the class by the CLR full name in the `type` header (ADR 0008), so both ends must share the type. The topic is `inventory.editions` with 3 partitions, keyed by edition id, and both services declare it, so whichever starts first creates it.
6. **Catalog serves the copy uncached**, at `GET /videos/{id}/editions`. The video entry is cached for 60 seconds (ADR 0009), which would add up to a minute of lag to a number that must lag only seconds.

## Alternatives considered

- **An event per change, written by each statement that changes an edition.** That means an outbox insert in seven places, the expiry sweep's single SQL statement among them, and a thousand events in a k6 run for a number Catalog overwrites. It also does not remove the need for a version. Kafka keeps order within a partition: "Events with the same event key … are written to the same partition", and a consumer reads that partition "in exactly the same order as they were written" ([Kafka, Introduction](https://kafka.apache.org/42/getting-started/introduction/)). But our relay can send one edition's events out of order. A send that Kafka did not acknowledge stays unsent and goes again on a later loop, while later rows of the same batch did go (`OutboxRelay`). Per-change events stay the right tool for facts another service acts on, such as Phase 1b's "copy assigned". A read copy only needs the latest state.
- **Catalog calls Inventory's API when it renders a video.** That needs no copy and has no lag, but Catalog's reads then fail whenever Inventory is down or slow. Under a sale, the hot edition's reads would also land on the database that is deciding the last copy.
- **Keep the last sent state in columns on `editions`.** That saves a table, but the pass would then update the row that every reserve of a hot edition is waiting to lock.

## Consequences

- Catalog's `copies_left` lags by about a pass interval plus a relay interval (1 s each by default) plus the consumer's time. That is within E-3's "a few seconds", and the k6 test still measures Inventory, not Catalog.
- Losing a snapshot is not fatal: the next change sends the full state. The relay also never drops a row.
- To rebuild Catalog's copy, delete Inventory's `edition_snapshots` rows. The next pass republishes every edition, and versions restart from 1, so first truncate Catalog's `editions` table, or its version guard drops the new rows.
- Each pass compares every edition with its snapshot. That is cheap for thousands of editions. Revisit it, for example with a "changed since" index, when editions number in the hundreds of thousands.
- Editions are never deleted, so no snapshot means "gone". When V-5 (remove a video) stops sales, it ends the sale, and the snapshot says so.
