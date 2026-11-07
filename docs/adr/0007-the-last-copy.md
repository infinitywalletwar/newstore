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

`tests/load/last-copy.js` on the Compose stack on the owner's Mac (Apple Silicon, OrbStack), 1000 virtual users for 10 copies, plus 10 for the expiry scenario; one run per strategy from a fresh volume. The collector ran with `verbosity: detailed` and Inventory exported metrics every 250 ms (an earlier ConditionalUpdate run without those settings measured reserve p50 325 ms and p99 432 ms; runs vary). Pool metrics come from the collector log.

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

## Lock order

Three paths lock a reservation row and an edition row, and each takes the reservation first:

- `ConfirmAsync` and `ReleaseAsync` lock the reservation with `SELECT … FOR UPDATE`, then update the edition (the counter `UPDATE`, or `ReturnCopyAsync`). A confirm of a reservation that has expired but is not swept yet takes this path too.
- `SweepAsync` is one statement with a data-modifying `WITH`. PostgreSQL documents: "The sub-statements in WITH are executed concurrently with each other and with the main query. Therefore, when using data-modifying statements in WITH, the order in which the specified updates actually happen is unpredictable." ([WITH, data-modifying statements](https://www.postgresql.org/docs/current/queries-with.html#QUERIES-WITH-MODIFYING)). If the sweep could lock an edition before a reservation, it could deadlock with a confirm.

The plan of the sweep (`EXPLAIN (COSTS OFF)`, PostgreSQL 18 in Testcontainers, 50 pending reservations of one edition):

```
Update on editions e
  CTE expired
    ->  Update on reservations
          ->  Index Scan using ix_reservations_status_expires_at on reservations
                Index Cond: (((status)::text = 'Pending'::text) AND (expires_at <= '…'::timestamp with time zone))
  ->  Nested Loop
        ->  Subquery Scan on x
              ->  HashAggregate
                    Group Key: expired.edition_id
                    ->  CTE Scan on expired
        ->  Index Scan using pk_editions on editions e
              Index Cond: (id = x.edition_id)
```

`Update on editions` reads `expired` through a `HashAggregate`. For a hashed aggregate the executor reads all the input before it returns a group: `ExecAgg` runs `if (!node->table_filled) agg_fill_hash_table(node);` and then `agg_retrieve_hash_table(node)`. The comments in the source say "ExecAgg for hashed case: read input and build hash table" and "ExecAgg for hashed case: retrieving groups from hash table" ([`nodeAgg.c`, REL_18_STABLE](https://github.com/postgres/postgres/blob/REL_18_STABLE/src/backend/executor/nodeAgg.c)). So in this plan every reservation row is locked before the first edition row. That is the executor's behaviour for the plan chosen, not a documented SQL guarantee; the plan can change with the data or the version.

Evidence: `LockOrderTests` runs 50 confirms of expired, unswept reservations and 5 sweeps at once, each on its own scope, for 20 rounds with a fresh edition each. It finished without a deadlock, every confirm answered `Expired`, and the edition ended with `copies_left` 50 and `copies_assigned` 0. A passing test shows no deadlock in 20 rounds; it does not prove there can be none.

If a deadlock ever appears, PostgreSQL aborts one of the transactions with SQLSTATE `40P01`. The sweep would retry on its next tick; a confirm would return 500 and the buyer retries. The candidate fix is two statements in one transaction, reservations first and editions second, which costs one more round trip per tick. We do not take it now: no deadlock has been observed.

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
