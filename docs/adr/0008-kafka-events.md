# Events: the outbox, at-least-once delivery and an idempotent consumer

## Status

Accepted

## Date

2026-11-06

## Context

Services learn about each other's data through Kafka events (README, architecture). An event must be published exactly when the business change commits: a change without its event leaves other services out of date, and an event without its change tells them something that did not happen. A database and Kafka cannot commit together. We also need to decide how events are keyed, encoded, retried and drained, and what a consumer does with a message it cannot handle.

## Decision

- **Transactional outbox.** A service writes the event to an `outbox_messages` row in the same database transaction as the business change (`IOutbox.Enqueue`, then the caller's `SaveChangesAsync`). A relay, a `BackgroundService` in each service, reads unsent rows with `FOR UPDATE SKIP LOCKED`, produces them with an idempotent, `Acks.All` producer and marks them sent. A message Kafka does not acknowledge is retried on the next loop, forever: the relay never drops an event.
- **At-least-once delivery, idempotent consumer.** The relay can crash after Kafka acknowledged a batch and before the database commit, so a message can be sent twice. The consumer records `(consumer group, message id)` in `inbox_messages` in the same transaction as the handler's writes, and skips a message it has seen. The message id is the event's `EventId`, a UUID v7 the outbox row also uses as its key.
- **The partition key is the aggregate's id** (an edition's id for edition events). Kafka orders messages within a partition and the producer hashes the key to pick the partition, so one aggregate's events reach a consumer in the order they were produced ([Kafka design, "The Producer"](https://kafka.apache.org/42/design/design/)).
- **JSON with `System.Text.Json`**, the CLR type's full name in a `type` header, the message id in `message-id`, the W3C `traceparent` in `traceparent`. Changes to an event are additive only: a new optional property is safe, because an old consumer ignores it. A consumer that gets a `type` it has no handler for on a topic logs it and moves on. A schema registry becomes worth its cost when a second language consumes the topics or a breaking change cannot be avoided.
- **A dead-letter topic per handled topic** (`<topic>.dlt`, one partition). A handler that throws is retried in place three times, one second apart; then the raw message goes to the dead-letter topic with `error`, `original-topic`, `original-partition` and `original-offset` headers, and the consumer moves on. One poisoned message must not stop a partition.
- **Draining on shutdown.** On a stop the consumer finishes the message in flight, stores its offset, commits and leaves the group. Offsets are stored only after the handler's transaction committed, and committed every 100 messages or every second.
- **Kafka is in no readiness check.** A service without Kafka still serves reads. The relay has a liveness check that fails only when its loop stalls, not when Kafka is down.

## Alternatives considered

- **Publish to Kafka after `SaveChanges`.** Two writes with no shared transaction: a crash between them loses the event, and a failed publish after the commit leaves the change without its event. The outbox moves the second write into the first's transaction.
- **Kafka transactions for exactly-once.** The Kafka documentation says exactly-once with the transactional producer and a `read_committed` consumer covers reading from and writing to Kafka topics, and that for other destination systems it "generally requires cooperation with such systems", the simplest being to store the consumer's position in the same place as its output ([Kafka design, "Message Delivery Semantics"](https://kafka.apache.org/42/design/design/#message-delivery-semantics)). Our output is a PostgreSQL row, so the transaction would not cover the part that matters. The inbox is that cooperation: the message id is stored with the output.
- **Change data capture (for example Debezium) instead of a polling relay.** Lower latency and no polling query, at the cost of another system to run and operate. At this scale, one second of polling is fine. (A claim about Debezium's behaviour is not verified here; this is a cost argument only.)
- **A message broker library (MassTransit, Wolverine) with its own outbox.** Less code to own, but the owner is learning the pattern, and the point of this project is to build it.
- **Avro or Protobuf with a schema registry now.** Expected benefits are smaller messages and enforced compatibility, at the cost of one more service to run and a build step for the schemas (not verified here). With one language and one team that changes both ends together, JSON is enough for now.

## Consequences

- Events reach Kafka a little late: up to `RelayInterval` (1 s) after the commit. Consumers must not assume an event arrives before the next request.
- A consumer sees a message at least once; the inbox makes the handler's effects happen once. A handler's side effects outside the database (an email, an HTTP call) are not covered and need their own idempotency key.
- The relay orders unsent rows by id and retries a failed message on the next loop while later messages may already have been sent, so an aggregate's events can be reordered after a Kafka outage. A consumer that needs strict order must compare versions. A permanently failing row (for example a topic name Kafka refuses) is retried forever at the head of the queue and takes a batch slot each loop; `attempts` and `last_error` make it visible.
- `outbox_messages` grows: sent rows stay. Cleaning them is not built in 1a.
- Every service with a consumer needs `inbox_messages` rows kept long enough to cover the longest redelivery (retention is not built in 1a).
- If Kafka is not reachable when a service starts, the topic creator tries five times, logs the failure and lets the service start, because a service without Kafka must still serve reads. The broker's `auto.create.topics.enable` defaults to `true` ("Enable auto creation of topic on the server") and `num.partitions` to `1` ([broker configs](https://kafka.apache.org/42/configuration/broker-configs/)), so the first produce or subscribe creates the topic with the broker's default partition count, not the declared one. That is why `TopicCreator` runs before the relay and the consumer.
