# 0013. Reservations for checkout: a hold owned by Inventory, and an id chosen by the caller

## Status

Accepted

## Date

2026-11-13

## Context

In the checkout saga (Task 7) Orders reserves a copy, sends the buyer to pay, and confirms the copy when the payment succeeds. Two problems show up that the Phase 1a reservation did not have.

- **The buyer pays slowly.** A reservation lives ten minutes. A buyer who has started paying (3-D Secure, a bank app) may need longer, and the copy must not go back on sale under their hands. The payment page has its own deadline: Stripe documents `expires_at` of a Checkout Session as "anywhere from 30 minutes to 24 hours after Checkout Session creation" ([Create a Checkout Session, `expires_at`](https://docs.stripe.com/api/checkout/sessions/create)). Our fake payment provider copies that shape.
- **A 201 can be lost.** Orders calls reserve, Inventory commits, and the response never arrives (timeout, restart). Orders does not know the reservation id, so it cannot confirm or release it, and a plain retry takes a second copy or meets `already-holding`.

## Decision

- **Inventory owns the hold and its limit.** `POST /reservations/{id}/hold` turns a `Pending` reservation that is still in time into `Held` and sets `ExpiresAt = now + HoldTtl` (`InventoryOptions.HoldTtl`, 45 minutes: inside Stripe's 30 minutes to 24 hours range, with room for a slow payment). A second hold changes nothing, so retrying a hold cannot stretch the deadline. Confirm accepts `Pending` or `Held` before `ExpiresAt`; release covers both; the sweep expires both (`status IN ('Pending', 'Held')`). The unique index on (edition, buyer) includes `Held`, so a buyer with a held copy cannot reserve a second one.
- **A payment that succeeds at minute 46 is refused.** Confirm of an expired `Held` reservation answers 409 `reservation-expired` and returns the copy, as it did for `Pending`. The saga must then refund; that is Task 7's compensation.
- **The caller supplies the reservation id.** `POST /editions/{id}/reservations` takes `{ reservationId, buyerId }`. Both strategies run the same algorithm (`ReserveSupport`; ConditionalUpdate sends fewer statements since the amendment below): look the id up before taking a copy (same edition and buyer: 200 with that reservation; another edition or buyer: 409 `reservation-id-taken`); and look it up again on every path where the reserve fails to take a copy. A retry that overlaps the original passes the first lookup (nothing committed yet), waits for the original on the edition row, and then finds no copy left, so without the second lookup it would answer `sold-out` to the request that just won the last copy. The second lookup is a new statement, and PostgreSQL says "Read Committed mode starts each command with a new snapshot that includes all transactions committed up to that instant" ([Transaction Isolation, Read Committed](https://www.postgresql.org/docs/current/transaction-iso.html#XACT-READ-COMMITTED)), so it sees the original's committed reservation. The failure paths are sold out, sale ended, no such edition, and a unique violation on `reservations` (after the rollback): found with this edition and buyer is `Existing` (a parallel request with the same id won), found with another is `IdTaken`, and not found is the original failure, or `AlreadyHolding` for the violation (it was the (edition, buyer) index). Orders chooses the id before the call and keeps it, so after a lost 201 it retries with the same id, or confirms or releases by id.
- **`CopyAssigned`** (`inventory.copies`, key `EditionId`) is enqueued in the transaction of the first confirm, so a repeated confirm publishes nothing.
- **Only `orders` may call the reservation endpoints** (`Policies.Client("orders")`, ADR 0012); `Auth:Audience` is `inventory`.

## Alternatives considered

- **Orders extends a timer.** Orders would call a "touch" endpoint, or reserve again, to keep the copy alive. Then two services each hold part of one rule (when does a copy go back on sale), the limit lives in whichever caller forgets to stop, and a crashed Orders leaves the copy held only as long as its last extension, which Inventory cannot predict. With the hold in Inventory the deadline is one column that the sweep reads.
- **An `Idempotency-Key` header with a stored response.** This is what Stripe does: "Stripe saves the status code and body of the first request made with a given idempotency key" and "you can remove keys from the system automatically after they're at least 24 hours old" ([Idempotent requests](https://docs.stripe.com/api/idempotent_requests)). It works for any endpoint without changing its body. It needs a table of keys with request fingerprints and responses, a pruning job, and a rule for a retry that arrives while the first request is still running. Here the reservation row already is the record: the id in the body is its primary key, the database enforces uniqueness, and the retry's answer is rebuilt from the row (so it shows the current status, not the status of the first answer). The price is that only this endpoint is safe to retry this way; confirm, hold and release are idempotent by their state rules instead.

## Consequences

- A retried reserve of an `Expired` or `Released` reservation returns 200 with that status. Orders must read `status`, not only the status code.
- The same id for another edition or buyer is a caller bug and fails loudly with 409.
- Reserve now runs one extra read (the id lookup) before taking a copy, and one more when it fails to take one. The load test (`tests/load/README.md`) shows the p99. For ConditionalUpdate the amendment below replaces both lookups and the edition read of a losing reserve with one read.
- The hold's 45 minutes is a default in options. It is not tied to the payment page's deadline in code; Orders sets the Checkout Session's expiry no later than the hold (Task 7).
- `ReservationTests` (both strategies) and `HoldTests` cover the rules; `Parallel_reserves_with_the_same_id_take_one_copy` sends 20 requests with one id and checks one reservation, one copy and no EF error record; `Parallel_reserves_with_the_same_id_for_the_last_copy` does it for an edition with one copy and expects one 201 and nineteen 200s, not `sold-out`.

## Amendment, 2026-11: ConditionalUpdate reserves in one statement

**Why.** The two id lookups above made every losing reserve send five statements instead of three. In the last-copy load test 1000 buyers share one 20-connection pool (`PostgresExtensions.cs`) and queue on one edition row, and 990 of them lose, so the extra round trips add up: CI's reserve p99 rose to 1.3 to 3.0 s and its threshold had to be raised to 3500 ms (#97, PR #98). ConditionalUpdate, the default strategy, now sends fewer statements; ConcurrencyToken is unchanged and still runs the algorithm described in the decision.

**The statements.** ConditionalUpdate takes the copy and inserts the reservation in one statement, with no explicit transaction:

```sql
WITH taken AS (
    UPDATE editions SET copies_left = copies_left - 1, locked_at = COALESCE(locked_at, @now)
    WHERE id = @edition AND copies_left > 0 AND ended_at IS NULL
    RETURNING id)
INSERT INTO reservations (id, edition_id, buyer_id, status, reserved_at, expires_at)
SELECT @id, id, @buyer, 'Pending', @now, @expires FROM taken
```

It inserts one row (a copy was taken) or none, or it fails with a unique violation on `reservations`. Only the last two cases read anything else. `ReserveRoundTripTests` counts the statements of one request through an EF Core interceptor:

| Request | Now | Before (Task 3) |
|---|---|---|
| Winner | the statement (1 row); the endpoint reads the edition for the body | lookup; `BEGIN` + `UPDATE`; `SAVEPOINT` + `INSERT`; `RELEASE SAVEPOINT`; `COMMIT`; edition read |
| Sold out or sale ended | the statement (no row); one read of the edition and the id together (`LEFT JOIN LATERAL`) | lookup; `BEGIN` + `UPDATE`; edition read; second lookup; `ROLLBACK` |
| No such edition | as above, plus a lookup of the id (the read found no edition row) | as above |
| Retry with the same id | the statement fails on the primary key (with copies left it first queues on the edition row behind every buyer in a burst, then breaks the key and gives the copy back); the lookup; the endpoint reads the copy number and the edition | lookup; copy number and edition reads |

Npgsql sends `BEGIN` and `SAVEPOINT` together with the next command, and `COMMIT`, `ROLLBACK` and `RELEASE SAVEPOINT` each as a command of their own: `Save` says "This method does not cause a database roundtrip to be made", `Init` queues `BEGIN` with `PrependInternalMessage`, and `Commit` calls `ExecuteInternalCommand` ([`NpgsqlTransaction.cs`, v10.0.3](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/NpgsqlTransaction.cs)). So a losing buyer now makes 2 round trips (5 under Task 3, 3 before it), and a winner holds the edition row lock for the length of one statement instead of across the `INSERT`, `RELEASE SAVEPOINT` and `COMMIT` round trips. A retry pays one more statement than under Task 3; only real retries pay it.

**Why each guarantee still holds.** The argument rests on five sentences of the PostgreSQL documentation:

- (S1) "Read Committed mode starts each command with a new snapshot that includes all transactions committed up to that instant" ([Read Committed](https://www.postgresql.org/docs/current/transaction-iso.html#XACT-READ-COMMITTED)).
- (S2) The would-be updater "will wait for the first updating transaction to commit or roll back", and then "The search condition of the command (the `WHERE` clause) is re-evaluated to see if the updated version of the row still matches the search condition" (same section).
- (S3) "If a conflicting row has been inserted by an as-yet-uncommitted transaction, the would-be inserter must wait to see if that transaction commits." ([Index Uniqueness Checks](https://www.postgresql.org/docs/current/index-unique-checks.html)).
- (S4) "If you do not issue a `BEGIN` command, then each individual statement has an implicit `BEGIN` and (if successful) `COMMIT` wrapped around it" ([Transactions](https://www.postgresql.org/docs/current/tutorial-transactions.html)).
- (S5) "Data-modifying statements in `WITH` are executed exactly once, and always to completion", and "`RETURNING` data is the only way to communicate changes between different `WITH` sub-statements and the main query" ([Data-Modifying Statements in WITH](https://www.postgresql.org/docs/current/queries-with.html#QUERIES-WITH-MODIFYING)).

1. **A retry with the same id returns the original, also when it overlaps the original on the last copy.** The retry's `UPDATE` waits on the edition row for the original and then re-checks `copies_left > 0` on the original's version (S2): no copy, so `taken` is empty and the `INSERT` gets no row (S5). The read that follows is a new statement, so it sees the original's committed reservation (S1) and answers `Existing`. When copies remain, the retry's `UPDATE` takes one and its `INSERT` breaks the primary key against the original; the statement fails, so nothing of it commits, the decrement included (S4), and the lookup finds the original. `Parallel_reserves_with_the_same_id_for_the_last_copy`, `Parallel_reserves_with_the_same_id_take_one_copy`, `Reserve_twice_with_the_same_id_returns_the_same_reservation`.
2. **An id of another edition or buyer is 409 `IdTaken`.** The primary key breaks (or, when no copy is left, the read finds the reservation) and `ForKnownId` compares edition and buyer. A request with that id on another edition that has not committed yet makes our `INSERT` wait for it (S3). `Reusing_an_id_for_another_edition_is_409`.
3. **One holding reservation per (edition, buyer).** The partial unique index breaks, the lookup finds no reservation under this id, so the answer is `already-holding`. `Same_buyer_cannot_hold_two_reservations_at_once`, `Buyer_with_a_held_reservation_cannot_reserve_again`, `Already_holding_logs_no_EF_error`.
4. **No overselling; copy numbers 1..N.** The conditional `UPDATE` and its row lock are unchanged (S2), and the reservation exists exactly when a copy was taken (S5, S4). Numbers still come from confirm. `A_hundred_buyers_for_ten_copies_get_exactly_ten`, and the load test: 10 sold, numbers 1 to 10.
5. **A retry of an `Expired` or `Released` reservation returns 200 with that status.** The row left the partial index, so a copy may be taken, and the primary key breaks: the decrement is undone (S4) and the lookup returns the row. `Retry_of_an_expired_or_released_reservation_returns_it_with_its_status` (both strategies) checks the status and that `copies_left` did not move.
6. **`SaleEnded` and `NotFound` answer as before.** The read sees `ended_at`, or no edition row; for no row the id is looked up as before. `Reserve_after_the_sale_ended_returns_409_sale_ended`, `Reserve_for_unknown_edition_returns_404`.

**The trap.** The lookup cannot move into the reserve statement (a `NOT EXISTS`, or a CTE that reads `reservations`). One statement has one snapshot: "All the statements are executed with the same snapshot (see Chapter 13), so they cannot "see" one another's effects on the target tables" (S5's page), and an `UPDATE` that waited "can see the effects of concurrent updating commands on the same rows it is trying to update, but it does not see effects of those commands on other rows in the database" (S2's page). The original's reservation is such an other row, so an overlapping retry would answer `sold-out` to the request that won. The committed original is found only by a statement that starts after the wait (S1), or by a unique check that waits for it (S3).

**Lock order.**

| Path | First | Then |
|---|---|---|
| Reserve, ConditionalUpdate | the edition row (the `UPDATE` in the statement) | the reservation indexes (the `INSERT`'s unique checks, S3) |
| Confirm, release, the expiry branch of hold | the reservation row (`SELECT … FOR UPDATE`) | the edition row (the counter `UPDATE` or `ReturnCopyAsync`), then the reservation row's `UPDATE` |
| Hold | the reservation row | nothing else |
| Sweep | the reservation rows (the `WITH`'s `UPDATE`) | the edition rows (ADR 0007, "Lock order") |

ConcurrencyToken's order is unchanged: its edition `UPDATE` and reservation `INSERT` go in one `SaveChanges`, in an order EF chooses (not checked here).

The new statement keeps the order reserve already had and holds the edition lock for a shorter time, but the order is opposite to the sweep's, and reserve and the sweep can deadlock in two cases, both of a buyer whose own reservation of edition E the sweep is expiring. Either way the reserve holds E's row, and its insert into the reservation indexes finds the buyer's old row, which the sweep's `UPDATE` has replaced but not committed. PostgreSQL: "if a conflicting valid row has been deleted by an as-yet-uncommitted transaction, the would-be inserter must wait for that transaction to commit or abort", and "an UPDATE always deletes the old row version before inserting the new version" ([Index Uniqueness Checks](https://www.postgresql.org/docs/current/index-unique-checks.html)). The sweep then waits for E.

1. **A new id**: the buyer reserves E again while the sweep expires their reservation. The insert waits in the (edition, buyer) index. Task 3 had this cycle too. We reproduced it on the Compose PostgreSQL 18.6 with two `psql` sessions, the sweep's order played as two statements with a pause: the reserve statement failed with `ERROR: deadlock detected`, `CONTEXT: while inserting index tuple … in relation "ix_reservations_edition_id_buyer_id"`, and the sweep committed (2 runs of 2).
2. **The same id**: a retry of reservation X while the sweep expires X. Under Task 3 the retry was answered from `FindByIdAsync` without touching the edition row; now its statement locks E first and its insert waits in the primary key, `reservations_pkey`. This is a cycle this amendment adds. The reviewer reproduced it 2 runs of 2 with the same two sessions (`ERROR: deadlock detected … while inserting index tuple … in relation "reservations_pkey"`), and `ReserveDeadlockTests` reproduces it in a test with two connections.

PostgreSQL "automatically detects deadlock situations and resolves them by aborting one of the transactions involved, allowing the other(s) to complete" ([Deadlocks](https://www.postgresql.org/docs/current/explicit-locking.html#LOCKING-DEADLOCKS)). The reserve statement is its own transaction and the caller supplies the id, so `ConditionalUpdateStrategy` sends it once more when it fails with SQLSTATE `40P01`: the first attempt left nothing behind, and the resend usually meets the sweep's committed result (case 1: the old row is `Expired` and outside the partial index, so the buyer reserves normally (not run in a test); case 2: the primary key breaks and the lookup answers the `Expired` reservation, 200). A second `40P01` propagates as a 500. It can happen: when the first attempt is aborted, the resend can reach E's row before the woken sweep, because an updater that meets an aborted `xmax` does not queue ([`HeapTupleSatisfiesUpdate`](https://github.com/postgres/postgres/blob/2f613719fa4eb47b0319f899220511465c5a55da/src/backend/access/heap/heapam_visibility.c#L694-L703)) and the sweep then restarts its wait ([`heap_update`](https://github.com/postgres/postgres/blob/2f613719fa4eb47b0319f899220511465c5a55da/src/backend/access/heap/heapam.c#L3724-L3742)); the resend then waits first, so its check can abort it (seen once in a loaded test run). `ReserveDeadlockTests` holds the resend back to test the single cycle. An aborted sweep runs again on its next tick. `ReserveRoundTripTests` proves the resend and its limit by injecting the failure (`A_deadlock_on_the_reserve_statement_is_resent_once`, `A_second_deadlock_is_not_resent`); `ReserveDeadlockTests` runs the same-id cycle for real: the retry's answer is 200 `Expired` and the statement went out twice. Without the resend that test fails with 500. The new-id case rests on the same resend and the psql run; it has no test of its own.

Confirm, release and the expiry branch of hold do not meet reserve this way: they change the reservation row only after their edition `UPDATE`, and a row that is only locked is still a valid conflicting row, so the reserve fails at once with `already-holding` (the same experiment with confirm's order: `duplicate key value violates unique constraint "ix_reservations_edition_id_buyer_id"` before confirm finished, 2 runs of 2). A hold that succeeds changes the reservation row and no edition row, so a reserve may wait for it, but the hold never waits for the reserve. Inserting the reservation before the decrement, in one transaction, would remove the cycles (in the same experiment its `INSERT` waited for the sweep to commit and then went through, 2 runs of 2), at the price of an `INSERT`, a `ROLLBACK` and a dead row for every losing buyer and two more round trips each. We keep the order and resend: the window is one sweep statement, and the resend costs nothing on any other path.

**Measured.** One session on the Mac (OrbStack), fresh stack per run, runs interleaved, `P99_MS=5000`; full table in `tests/load/README.md`. Every run sold 10 copies, numbered 1 to 10, with 990 `sold-out` and no failed request.

| Inventory | Runs | reserve p99 | reserve median |
|---|---|---|---|
| Task 3 (a77c6d1) | 4 | 0.99 to 1.26 s | 736 to 988 ms |
| this amendment (d956d3a) | 3 | 0.44 to 0.73 s | 376 to 580 ms |
| before Task 3 (c63e6c0) | 3 | 0.30 to 0.66 s | 218 to 502 ms |

CI's threshold stays at `P99_MS=3500`, by the owner's decision. Three CI runs of one head (49cba7a, run 43358510750) gave a reserve p99 of 1.95 s (pass), 1.62 s (pass) and 2.75 s (fail at 2000): the runner is slower and noisier than the Mac, so CI guards against gross regressions, and the rule that p99 stays under 1 s is checked on the Mac (Task 9).
