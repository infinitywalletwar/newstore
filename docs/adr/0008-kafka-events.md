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
- `outbox_messages` and `inbox_messages` would grow with every event. See "Retention" below.
- If Kafka is not reachable when a service starts, the topic creator tries five times, logs the failure and lets the service start, because a service without Kafka must still serve reads. The broker's `auto.create.topics.enable` defaults to `true` ("Enable auto creation of topic on the server") and `num.partitions` to `1` ([broker configs](https://kafka.apache.org/42/configuration/broker-configs/)), so the first produce or subscribe creates the topic with the broker's default partition count, not the declared one. That is why `TopicCreator` runs before the relay and the consumer.

## Retention

`MessagingCleanupService<TContext>`, a `BackgroundService` registered by `AddMessaging`, deletes old rows every `Messaging:CleanupInterval` (5 minutes; the first pass runs after the first interval). Each pass is two `DELETE` statements, one per table, run with `ExecuteDeleteAsync` ([EF Core, "ExecuteUpdate and ExecuteDelete"](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)).

- **Outbox: 1 day** (`Messaging:OutboxRetention`). Only rows with `published_at` set are deleted. A published row is an audit trail, and one day leaves time to look at what was sent. A row that was never published stays, however old: it is still waiting for Kafka.
- **Inbox: 7 days** (`Messaging:InboxRetention`). This equals Kafka's default topic retention: `log.retention.hours` has the default 168 ([broker configs](https://kafka.apache.org/42/configuration/broker-configs/#brokerconfigs_log.retention.hours)), and its description is "The number of hours to keep a log file before deleting it". After the topic dropped a message, the topic cannot deliver it again, so the inbox row has nothing left to guard against.
- **Consequence.** An inbox row is the proof that a message was handled. Once it is deleted, a duplicate of that message is handled again. A replay of messages older than seven days (for example a consumer group reset to the earliest offset on a topic whose retention is longer than the default) is therefore handled again. A service that replays must raise `Messaging:InboxRetention` first. A topic with a longer `retention.ms` than the default needs a longer inbox retention for the same reason (not verified against a running broker).
- **Alternative: one batched `DELETE` with a limit.** PostgreSQL's `DELETE` has no `LIMIT` clause, so a batch selects a limited set of keys in a subquery or CTE and deletes those ([PostgreSQL, DELETE](https://www.postgresql.org/docs/current/sql-delete.html)). Batching keeps each transaction short and the lock window small when a table has millions of old rows. At this project's volume one statement per table every five minutes deletes a few thousand rows at most, so the simpler form is enough. Revisit it when a pass takes noticeably long (not measured here).

## Amendment, 2026-11: transient failures are retried without a limit

The original rule dead-lettered every handler that failed three times. Nothing reads `<topic>.dlt`, so a message that failed only because a dependency was down (Orders calling Inventory, for two minutes) was lost to the buyer's process. The rule is now:

- **A transient failure is retried until it succeeds.** `TransientFailures.IsTransient` accepts `TransientFailureException`, `HttpRequestException`, `TimeoutException` and a `DbException` whose `IsTransient` is true, found on the exception or any inner exception. The consumer logs a warning, seeks back to the message, waits `min(TransientRetryPause × 2^(attempt−1), MaxTransientRetryPause)` (1 s and 30 s by default), stores no offset and keeps polling. It counts the attempts per topic, partition and offset and clears the count when the message succeeds or its partition is revoked. The counter `messaging.handler.transient_retries` (tag `messaging.destination.name`) shows it.
- **Any other exception keeps the old path:** `MaxHandlerAttempts` tries, then `<topic>.dlt`. Only a message that can never succeed goes there.
- **The consumer keeps polling rather than sleeping inside the handler loop.** `max.poll.interval.ms` (default 300000): "If this interval is exceeded the consumer is considered failed and the group will rebalance in order to reassign the partitions to another consumer group member" ([librdkafka CONFIGURATION.md](https://github.com/confluentinc/librdkafka/blob/master/CONFIGURATION.md)). The longest pause is 30 s, so the consumer stays in the group however long the dependency is down.

**Cost.** The consumer handles one message at a time and waits during the pause, so a down dependency stops every partition the instance holds, not only the one with the failing message. That is acceptable while each service consumes few topics. Revisit it when one service consumes topics with unrelated dependencies.

**Alternatives considered**

- **Retry topics.** The failed message is forwarded to a retry topic with a back-off timestamp, so the main topic keeps moving ([Spring for Apache Kafka, "How the Pattern Works"](https://docs.spring.io/spring-kafka/reference/retrytopic/how-the-pattern-works.html): "the message is forwarded to a retry topic with a back off timestamp"). The same page says "By using this strategy you lose Kafka's ordering guarantees for that topic." Our saga handlers rely on the order within a key, and the extra topics and consumers are more to run. Retry topics fit when throughput matters more than order.
- **Dead-letter topic plus a replay tool.** Keeps the consumer moving and loses nothing, but needs a tool and a person to notice and replay, and the buyer waits until they do. A tool to inspect and replay the DLT is still worth building for permanent failures (not verified; a cost argument only).
- **Retry every failure forever.** A poison message, one that fails the same way each time, would block its partition for good, and everything behind it with it. Classifying the failure is what makes unlimited retry safe.
