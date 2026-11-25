# 0022. Positions are a stream: produced without the outbox, consumed in batches without the inbox

## Status

Accepted

## Date

2026-11-25

## Context

Phase 3 delivers a DVD by a simulated courier, and the buyer watches it move on a map. The courier is a role of Delivery itself (`delivery-courier`), not another service. Every `Delivery:Courier:TickInterval` (2 s) it produces one `CourierPosition` per shipment on the road to `delivery.positions` (6 partitions, key = shipment id), and Delivery's API role stores it and pushes it to the browser.

A position is unlike the events of ADR 0008 in two ways. The next one, 2 s later, replaces it: the map needs the newest point, and the path only draws the points it has. And the courier can make any position again from its own state: the position with sequence number `n` is computed from the trip's start time and the timeline (the plan's "The timeline"), so a courier that restarts, or another courier that takes over the shipment's lease, produces the same `seq` for the same moment. Nothing is lost for good when one message is.

ADR 0008 says every event goes through the outbox and every consumer through the inbox. Applied here, that is one outbox row, one relay hop and one inbox transaction per position, at 0.5 a second per shipment, for data nobody needs protected.

## Decision

Positions are a **stream**, the one kind of message that bypasses the outbox and the inbox. `NewStore.Messaging` gets a stream path next to the event path (AGENTS.md, "Streams").

- **Producer: `IStreamProducer.ProduceAsync`, awaited.** It sends through the service's one `KafkaProducer`, the same producer the relay uses: `EnableIdempotence = true`, `Acks = Acks.All`, `MessageTimeoutMs` = `Messaging:ProduceTimeout`. librdkafka's [CONFIGURATION.md](https://github.com/confluentinc/librdkafka/blob/master/CONFIGURATION.md#global-configuration-properties) says of `enable.idempotence`: "When set to `true`, the producer will ensure that messages are successfully produced exactly once and in the original produce order", and of `acks` ([topic properties](https://github.com/confluentinc/librdkafka/blob/master/CONFIGURATION.md#topic-configuration-properties)): "*-1* or *all*=Broker will block until message is committed by all in sync replicas (ISRs)". The call completes when Kafka acknowledged the message and throws `ProduceException<string, string>` when it did not within the timeout. The courier marks a trip finished (`courier_finished_at`) only after the last position's acknowledgement, so the one position that the next tick would not replace is never lost.
- **Consumer: `IStreamHandler<TMessage>`, in batches, in a group of its own.** `AddStreamHandler<TMessage, THandler>(topic)` starts one `KafkaStreamConsumer<TMessage>` with group id `<ClientId>.<topic>` (Delivery's: `NewStore.Delivery.delivery.positions`). It gathers up to `Messaging:StreamBatchSize` (200) messages or for `Messaging:StreamBatchWindow` (200 ms) from the batch's first message, calls the handler once per batch, and commits each partition's highest offset + 1 after the handler returned. No inbox and no dead-letter topic.
- **Idempotent by a natural key.** Delivery's handler inserts positions with `ON CONFLICT DO NOTHING` on `(shipment_id, seq)` and moves a shipment with `WHERE last_seq < @seq`, so a position read twice, or an old one read after a newer one, changes nothing.
- **Failures.** A message of another type, or one that does not deserialise, is skipped and logged (Debug and Warning). A handler that throws has the whole batch read again after a pause that grows with each attempt (`TransientPauseFor`, at most `Messaging:MaxTransientRetryPause`), without a limit.
- It uses the lease of ADR 0020 for the courier's claim on a shipment (Task 4): only one courier produces a shipment's positions at a time, and a dead courier's shipments are taken over when the lease runs out.

## Alternatives considered

- **The outbox for positions.** It is what ADR 0008 chose for events: a row in `outbox_messages` in the business transaction, then a relay that polls every `RelayInterval` (1 s), produces the row and marks it sent, so an event "reach[es] Kafka a little late: up to `RelayInterval` (1 s) after the commit" (ADR 0008, Consequences). For positions that is a database write and an update per position, about 0.5 a second per shipment, plus up to a second of extra delay on a map that refreshes every 2 s. The outbox exists so that a business change and its event commit together; a position has no business change to commit with, and a lost one is replaced on the next tick.
- **The inbox for consumption.** The event consumer handles one message per transaction and records its id (ADR 0008, "At-least-once delivery, idempotent consumer"). The inbox is how a consumer without a natural key turns at-least-once into once. A position has a natural key, `(shipment_id, seq)`, and the `last_seq < @seq` guard already makes a repeat harmless, so the inbox would only add a row and a transaction per message. Batching 200 positions into one transaction is where the stream's throughput comes from.
- **Sharing the events' consumer group.** The event consumer handles one message at a time and pauses its whole loop while a transient failure is retried (ADR 0008, amendment, "Cost": "a down dependency stops every partition the instance holds"). In one group, one slow event handler would hold every position, and one position batch would hold the events. A group of its own lets each progress, and lag, on its own.
- **Kafka transactions.** The Kafka documentation says exactly-once with transactions covers reading from and writing to Kafka topics, and that for other destinations it "generally requires cooperation with such systems" ([Kafka design, "Message Delivery Semantics"](https://kafka.apache.org/42/design/design/#message-delivery-semantics), quoted in ADR 0008). The handler's output is PostgreSQL rows, so a transaction would not cover it; the natural key is that cooperation.

## Consequences

- **A crash can lose positions.** A courier that dies between ticks loses what it had not produced yet, and a failed produce of an ordinary tick is not retried: the plan's courier logs it and leaves its last seq as it was, so the next tick produces the position of that moment instead. Only the last position waits for its acknowledgement before the trip counts as finished.
- **At-least-once, not exactly once.** A crash after the handler and before the commit, or a failed commit, reads a batch again. A failed commit is logged and its offsets are sent with the next commit; the batch is not handled again because of it.
- **A handler bug stalls the stream until it is fixed.** A batch that throws is retried without a limit, so a handler that always throws keeps its partitions where they are while new positions pile up behind them. That is the price of never dead-lettering a position. Consumer lag shows it; Phase 4's consumer-lag dashboard is where it becomes visible.
- **The handler must never throw because of one bad message.** One bad position would otherwise stop all the others in its batch for good. The handler drops it and logs it; it throws only when the whole batch should be retried, such as when the database is down.
- **A new stream needs its own decision record** that says why losing one message is harmless (AGENTS.md, "Streams"). Everything else stays an event and goes through the outbox (ADR 0008).
