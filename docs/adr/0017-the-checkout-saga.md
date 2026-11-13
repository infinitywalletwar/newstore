# 0017. The checkout saga: Orders orchestrates, with synchronous commands and asynchronous outcomes

## Status

Accepted

## Date

2026-11-13

## Context

Buying a copy touches three services that each own a database: Inventory (the copy), Payments (the money) and Orders (the buyer's order). No transaction spans them. The steps and what can go wrong:

- **Reserve.** The buyer must learn at once whether they got the copy. The reply can be lost after Inventory committed.
- **Pay.** The buyer pays on the provider's page, in their own time. The money can arrive after the reservation ran out (ADR 0013), or never.
- **Confirm or give back.** A payment that succeeds must end in a copy or in a refund. Inventory can be down at that moment.

Microsoft's description of the pattern: a saga breaks a transaction "into a sequence of local transactions", and "if a local transaction fails, the saga performs a series of compensating transactions to reverse the changes that the preceding local transactions made" ([Saga pattern](https://learn.microsoft.com/azure/architecture/patterns/saga)). The page also says that "data can't be rolled back because saga participants commit changes to their respective databases", and that retryable steps after the pivot "are idempotent and help ensure that the saga can reach its final state". Both shape the design below.

## Decision

**Orders is the orchestrator.** The saga's state is the `Order` row (`Reserved`, `Paying`, `Completed`, `Expired`, `Failed`, `Refunding`, `Refunded`). Orders sends commands and reacts to outcomes:

- **Commands are synchronous HTTP calls from Orders**, because a person waits for the answer: reserve (on Buy), hold and create payment (on Pay). Each is safe to repeat: the reservation id is chosen by Orders, hold is idempotent, and Payments has one payment per order id.
- **Outcomes arrive as events from Payments** (`PaymentSucceeded`, `PaymentFailed`, `PaymentRefunded`), through the inbox, once per message id. A handler may call Inventory (confirm, release); each of those calls is idempotent too, because the handler's transaction can fail after the call went through and the message will run again.
- **The one event Orders publishes is `RefundRequested`**, through the outbox in the same transaction as the move to `Refunding`.
- **The timeout step is the provider's, not a timer in Orders.** FakePay's checkout session expires 30 minutes plus Payments' 30 s margin after the payment is created (`PaymentsOptions.SessionLifetime`, `ExpiryMargin`); Payments then publishes `PaymentFailed`, and Orders releases the copy. Inventory's hold lasts 45 minutes (`InventoryOptions.HoldTtl`): it is the upper bound, not the timeout. So a `Paying` order normally ends about 30.5 minutes after Pay, while `reservedUntil` shows the hold's end. A payment that succeeds after the hold ended is the "late payment": confirm answers 409, and the compensation is a refund.
- **Compensation is a new forward step, not a rollback.** Orders cannot undo Payments' commit; it asks Payments to do the opposite (`RefundRequested`), and the order stays `Refunding` until `PaymentRefunded` arrives.
- **Concurrent writers meet at `xmin`.** A pay request and a payment event can change the same order. `Order.Version` maps PostgreSQL's `xmin`, so the later save fails. The pay endpoint reloads and answers from the stored state; a handler's save that loses fails with `DbUpdateConcurrencyException`, which Messaging does not count as transient, so the message gets another run (`MaxHandlerAttempts`, 3 by default) that reads the new state.

## Alternatives considered

- **Choreography** (no orchestrator; each service reacts to the others' events). Microsoft lists its benefits as good for "simple workflows that have few services", and its drawbacks as "it's difficult to track which commands each saga participant responds to" and "integration testing is difficult because all services must run" ([Saga pattern, Choreography](https://learn.microsoft.com/azure/architecture/patterns/saga#choreography)). Here the buyer waits for reserve and pay, so the first two steps are request and reply anyway; making them events would add a way to say "sold out" back to a browser. Orchestration keeps the whole flow in `OrderEndpoints` and `Saga/`. The price: Orders knows about Inventory and Payments (the page lists "introduces a point of failure because the orchestrator manages the complete workflow" as the orchestration drawback).
- **MassTransit state machines.** The documentation describes "a long-lived transaction managed by a coordinator" with persisted state ([MassTransit sagas](https://masstransit.massient.com/documentation/patterns/saga)). It would replace the `Order` status switch and the handlers, but it brings its own saga store and message conventions next to our outbox and inbox (ADR 0008), and the same page states "MassTransit v9 is now a commercial product". We would adopt a second messaging model for a three-step flow.
- **Temporal workflows.** Workflows are code whose steps are replayed from a recorded history, so the code "must make the same decisions when given the same history" and must not read the clock or make network calls outside an Activity ([Temporal Workflows](https://docs.temporal.io/workflows)). It gives timers and retries for free and is the strongest option for long flows. It is another service to run and a different programming model for a flow that has one timer (the provider's) and three states that matter. Revisit it if the flow grows many steps or its own timers.
- **Two-phase commit.** PostgreSQL provides `PREPARE TRANSACTION` for "an external transaction manager to perform atomic global transactions across multiple databases or other transactional resources", says it "is not intended for use in applications", and warns that leaving transactions prepared for a long time interferes with `VACUUM` ([PREPARE TRANSACTION](https://www.postgresql.org/docs/current/sql-prepare-transaction.html)). A buyer paying on a provider's page holds a transaction open for tens of minutes, and the provider is not a participant in any transaction manager. It does not fit this flow.

## Consequences

- **A database transaction stays open during an HTTP call in a handler.** `PaymentSucceeded` confirms with Inventory inside the inbox transaction, so the row and the connection are held while Inventory answers. What bounds it: Orders' clients use the standard resilience handler, which Microsoft documents with a total timeout of 30 s and an attempt timeout of 10 s ([Standard resilience handler defaults](https://learn.microsoft.com/dotnet/core/resilience/http-resilience#standard-resilience-handler-defaults)). When Inventory is down, the handler throws `TransientFailureException` after at most that, the transaction rolls back, and Messaging retries the message with a pause. The alternative, confirming outside the transaction, would let a crash between the confirm and the commit lose the knowledge that the copy was taken; confirm is idempotent, so the repeat is harmless and the open transaction is the cheaper evil.
- **Crash between reserve and saving the order.** Inventory holds a `Pending` reservation Orders never recorded. The buyer's retry gets a new reservation id and Inventory answers `already-holding` until the unused reservation expires (10 minutes). Nothing is lost or double-charged, and the buyer waits. A retry of the same request within one call reuses the id, and that is the case the resilience handler covers.
- **Polly's rejections.** `BrokenCircuitException` and `TimeoutRejectedException` become `TransientFailureException` in the clients (`ServiceCall`), as in ADR 0014 point 8, so a handler waits for a service that is down instead of being dead-lettered.
- **Unknown order.** A payment event for an order Orders does not have throws `InvalidOperationException`, and the message is dead-lettered: waiting cannot make the order appear.
- **A buyer who clicks Buy twice at the same instant** can reach Inventory twice with two reservation ids; the second gets `already-holding`. The open-order check handles the sequential case, and the parallel one ends in a 409 the buyer can retry into the open order.
- Tests: `BuyTests`, `PayTests`, `SagaTests` and `ClientTests` in `tests/Orders.Tests`.
