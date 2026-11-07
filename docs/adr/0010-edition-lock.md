# 0010. The edition lock: a column set by reserve, checked by the edit's WHERE

## Status

Accepted

## Date

2026-11-07

## Context

Story E-2: a creator cannot change an edition's price or number of copies once its first copy is reserved, and can end the sale early, which withdraws the unsold copies. The edit and a reserve can arrive at the same moment, so "once reserved" must hold without a lock on the whole table and without a window in which both succeed.

## Decision

- `editions.locked_at` is set by the first successful reserve (`locked_at = COALESCE(locked_at, now)` in the same `UPDATE` that takes the copy; the ConcurrencyToken strategy sets it in the same `SaveChanges`). Nothing clears it: a released or expired reservation does not unlock, because the story says "once its first copy is reserved".
- An edit is one statement: `UPDATE editions SET … WHERE id = … AND locked_at IS NULL AND ended_at IS NULL`. One row updated means the edit applied; zero means the edition is missing, ended (409 `sale-ended`) or locked (409 `edition-locked`), which the endpoint tells apart with a read.
- The check lives in the `WHERE` of the `UPDATE` because of how PostgreSQL treats a second updater under Read Committed: "the would-be updater will wait for the first updating transaction to commit or roll back (if it is still in progress). … If the first updater commits, … it will attempt to apply its operation to the updated version of the row. The search condition of the command (the WHERE clause) is re-evaluated to see if the updated version of the row still matches the search condition." ([Transaction Isolation, Read Committed](https://www.postgresql.org/docs/current/transaction-iso.html#XACT-READ-COMMITTED)). The reserve and the edit both update the edition row, so whichever commits second sees the other's `locked_at` (or `copies_total`). That sentence is about a column of the row being updated. The page also warns that an updating command "can see the effects of concurrent updating commands on the same rows it is trying to update, but it does not see effects of those commands on other rows in the database". So the sentence says nothing that covers a subquery on another table, so `AND NOT EXISTS (SELECT … FROM reservations …)` would rest on behaviour the page does not describe, and we do not use it. (Under ConcurrencyToken the edit also changes `xmin`, so a reserve that read the row first fails its own `UPDATE` and retries.)
- Ending the sale (`POST /editions/{id}/end-sale`) sets `ended_at` and `copies_left = 0` once; a second call changes nothing and answers 200. Reserve on an ended edition answers 409 `sale-ended`, not `sold-out`.
- A Pending reservation made before the end stays valid: the buyer can still confirm it until it expires. When an expired or released reservation returns its copy on an ended edition, the copy stays withdrawn (`ReturnCopyAsync` and the sweep add nothing when `ended_at` is set).

## Consequences

- A creator cannot unlock an edition by getting all reservations released. That is the intent of the story, and it keeps the rule to a single column.
- After an end, `copies_left` can be 0 while `copies_total - copies_assigned > 0`: `copies_left` counts copies on sale, not copies unsold. Pending reservations that later confirm raise `copies_assigned` without touching `copies_left`.
- The `BackfillEditionLock` migration sets `locked_at` from the earliest reservation (any status) where it is still NULL, so editions reserved before this change are locked too. It is a migration of its own, because `AddEditionLockAndEnd` was already on `main`, and a database that has applied a migration never runs it again. `MigrationTests` proves the backfill.
- `copies_left == copies_total` holds for every unlocked edition, so the edit sets both to the new count without arithmetic.
- The edit and the reserve contend on the same row as two reserves do; no new lock, no new index.
- `EditionTests` and the abstract `ReservationTests` (both strategies) cover the rules; `Edit_and_reserve_racing_never_leave_the_count_inconsistent` races five reserves against one edit for 20 rounds and checks `copies_total - copies_left` equals the Pending plus Confirmed reservations. As with ADR 0007, a passing race shows no failure in 20 rounds, not a proof.

## Alternative considered

**`SELECT … FOR UPDATE` on the edition, then a second statement that checks `reservations`, then the edit.** This is also correct under Read Committed: the lock makes a reserve wait, and the second statement takes a new snapshot after the lock is granted, so it sees every committed reservation (the same page: "Read Committed mode starts each command with a new snapshot that includes all transactions committed up to that instant"). It costs two or three statements per edit, and it depends on reservation rows never being deleted: delete one, and the check would let an edit through. With `locked_at` the fact is on the edition row itself, so nothing else has to be true.
