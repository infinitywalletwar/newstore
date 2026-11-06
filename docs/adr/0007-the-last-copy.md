# 0007. The last copy: one conditional UPDATE, measured against a concurrency token

## Status

Accepted

## Date

2026-11-06

## Context

Requirement C-4: when a thousand buyers go for the last ten copies of an edition, exactly ten get one, nobody gets two, and nobody sees a server error. Reserving must take a copy off the shelf (`copies_left - 1`) and record who holds it, and the two must happen together or not at all. Copy numbers (C-3) are a separate matter, handled at confirm time: see the walkthrough of the pull request.

Inventory ships two interchangeable strategies behind `IReserveStrategy`, chosen by `Inventory:ReserveStrategy`, so the decision rests on a measurement and not on taste.

## Decision

`ConditionalUpdate` is the default: inside one transaction, `UPDATE editions SET copies_left = copies_left - 1 WHERE id = … AND copies_left > 0`, then insert the reservation. Zero rows updated means sold out (or unknown edition). `ConcurrencyToken` (read the edition, decrement in memory, save with PostgreSQL's `xmin` as the concurrency token, retry on conflict) stays as the comparison and as a switch.

One buyer holds at most one live copy per edition because of a partial unique index on `reservations (edition_id, buyer_id) WHERE status IN ('Pending', 'Confirmed')`. A duplicate insert fails with SQLSTATE `23505`, and the rollback takes the decrement with it.

## Measurements

`tests/load/last-copy.js` on the Compose stack on the owner's Mac (Apple Silicon, Docker Desktop), 1000 virtual users for 10 copies, plus 10 for the expiry scenario; one run per strategy from a fresh volume. The collector ran with `verbosity: detailed` and Inventory exported metrics every 250 ms (an earlier ConditionalUpdate run without those settings measured reserve p50 325 ms and p99 432 ms; runs vary). Pool metrics come from the collector log.

| | ConditionalUpdate | ConcurrencyToken |
|---|---|---|
| `copies_confirmed` / `sold_out` | 10 / 990 | 10 / 990 |
| numbers on the first edition | exactly 1..10 | exactly 1..10 |
| reserve p50 / p99 | 554 ms / 738 ms | 4.18 s / 4.34 s |
| confirm p99 | 523 ms | 349 ms |
| `inventory.reserve.attempts`: sum / count / max | 1020 / 1020 / 1 | 9469 / 1020 / 11 |
| `contended` (503) replies | 0 | 0 (the most any reserve needed was 11 of 20 attempts) |
| pool `pending_requests`, highest sample (request pool, 20 connections) | 990 | 981 |
| pool timeouts | none: `http_req_failed` 0% | none: `http_req_failed` 0% |
| k6 thresholds | all pass | all pass except reserve p99 < 1000 ms |

Notes on reading the table:

- The Npgsql metrics in this stack are named `db.client.connection.npgsql.pending_requests` and so on. No `db.client.connection.timeouts` metric was exported by either run, so the evidence for "no timeouts" is `http_req_failed` at 0%: a request that timed out waiting for a connection would have been a 5xx.
- `pending_requests` is high for both strategies: 1000 requests arrive at once and the pool holds 20 connections, so they queue for a connection before they ever reach the row. The pool, not the strategy, is the first queue; ADR 0002 sets its size.
- Both strategies are correct. They differ in work: ConcurrencyToken needed 9.3 database attempts per reserve on average, each a read and a write in their own round trip, and p99 of 4.3 s. ConditionalUpdate needed one.
- One run of each: the numbers show the order of magnitude, not a benchmark.

## Alternatives considered

- **Pessimistic lock: `SELECT … FOR UPDATE` on the edition row, then update.** PostgreSQL blocks other transactions that try to update or lock the row until the first transaction ends ([Explicit Locking, row-level locks](https://www.postgresql.org/docs/18/explicit-locking.html)). It is correct, and it queues on the same row as the conditional UPDATE does. It costs one more round trip with the lock held. We did not measure it.
- **`SERIALIZABLE` isolation with retries.** The database detects the conflict and fails a transaction with SQLSTATE `40001`; the page says applications using this level must be prepared to retry ([Transaction Isolation, Serializable](https://www.postgresql.org/docs/18/transaction-iso.html)). That is the ConcurrencyToken cost again, with the retries decided by the database. We did not measure it.
- **An advisory lock per edition.** Advisory locks have application-defined meaning and the system does not enforce them; the application must use them correctly, and a session-level lock survives a rolled-back transaction ([Explicit Locking, advisory locks](https://www.postgresql.org/docs/18/explicit-locking.html)). A lock per edition would serialise reserves exactly as the row lock already does, so it adds a rule to remember and no capability.
- **A counter in Redis (`DECR`).** `DECR` decrements by one and returns the new value ([DECR](https://redis.io/docs/latest/commands/decr/)), so the decrement itself is easy. The reservation row lives in PostgreSQL, so the two cannot share a transaction: a crash between them leaves the counter and the table disagreeing, and something would have to reconcile them. (Our reasoning, not a measurement.)

## Consequences

- Reserve is one `UPDATE` and one `INSERT` in one transaction, with no retry loop to tune. Under contention the cost is a queue on the edition's row, not repeated work.
- No gaps in copy numbers: reserve never assigns a number. Confirm takes the next one from `editions.copies_assigned` with `UPDATE … RETURNING` inside the confirming transaction.
- The edition row is the hot spot: every reserve and every confirm of one edition queues on it. One very popular edition is limited by how fast one row can be updated, whatever the number of Inventory instances. Splitting the counter over several rows would lift that, at the price of a more complex model; nothing in the measurements calls for it yet.
- ConcurrencyToken's retries grow with contention: here the average was 9.3 attempts and the worst 11, close to the default limit of 20. At higher contention it would start answering 503 `contended`.
- The 1000-buyer test is part of CI (`compose-smoke`), with a looser latency threshold for the slower runner.
