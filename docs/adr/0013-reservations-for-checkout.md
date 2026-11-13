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
- **The caller supplies the reservation id.** `POST /editions/{id}/reservations` takes `{ reservationId, buyerId }`. Both strategies run the same algorithm (`ReserveSupport`): look the id up before taking a copy (same edition and buyer: 200 with that reservation; another edition or buyer: 409 `reservation-id-taken`); on a unique violation, roll back, look the id up again, and answer `Existing` (a parallel request with the same id won), `IdTaken`, or, when nothing has that id, `AlreadyHolding` (the violation was the (edition, buyer) index). Orders chooses the id before the call and keeps it, so after a lost 201 it retries with the same id, or confirms or releases by id.
- **`CopyAssigned`** (`inventory.copies`, key `EditionId`) is enqueued in the transaction of the first confirm, so a repeated confirm publishes nothing.
- **Only `orders` may call the reservation endpoints** (`Policies.Client("orders")`, ADR 0012); `Auth:Audience` is `inventory`.

## Alternatives considered

- **Orders extends a timer.** Orders would call a "touch" endpoint, or reserve again, to keep the copy alive. Then two services each hold part of one rule (when does a copy go back on sale), the limit lives in whichever caller forgets to stop, and a crashed Orders leaves the copy held only as long as its last extension, which Inventory cannot predict. With the hold in Inventory the deadline is one column that the sweep reads.
- **An `Idempotency-Key` header with a stored response.** This is what Stripe does: "Stripe saves the status code and body of the first request made with a given idempotency key" and "you can remove keys from the system automatically after they're at least 24 hours old" ([Idempotent requests](https://docs.stripe.com/api/idempotent_requests)). It works for any endpoint without changing its body. It needs a table of keys with request fingerprints and responses, a pruning job, and a rule for a retry that arrives while the first request is still running. Here the reservation row already is the record: the id in the body is its primary key, the database enforces uniqueness, and the retry's answer is rebuilt from the row (so it shows the current status, not the status of the first answer). The price is that only this endpoint is safe to retry this way; confirm, hold and release are idempotent by their state rules instead.

## Consequences

- A retried reserve of an `Expired` or `Released` reservation returns 200 with that status. Orders must read `status`, not only the status code.
- The same id for another edition or buyer is a caller bug and fails loudly with 409.
- Reserve now runs one extra read (the id lookup) before taking a copy. The load test (`tests/load/README.md`) shows the p99.
- The hold's 45 minutes is a default in options. It is not tied to the payment page's deadline in code; Orders sets the Checkout Session's expiry no later than the hold (Task 7).
- `ReservationTests` (both strategies) and `HoldTests` cover the rules; `Parallel_reserves_with_the_same_id_take_one_copy` sends 20 requests with one id and checks one reservation, one copy and no EF error record.
