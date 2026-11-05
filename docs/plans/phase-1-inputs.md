# Phase 1 planning inputs

The Phase 0 reviews found these items, and they belong to Phase 1. The Phase 1 plan takes each one, or records why it doesn't, and its PR deletes this file.

## Decide before writing the plan

- **Split Phase 1.** It holds four services, the gateway, sign-in, the outbox, Kafka, the saga, four pages and the k6 test: weeks with no "done when" in between, the README's anti-goal. Split it in two:
  - **1a, the last copy:** Inventory alone, with editions and reservations seeded by an endpoint, no auth and no Kafka. Done when the k6 test with 1,000 buyers and 10 copies sells exactly 10, and an ADR compares the concurrency token with a conditional `UPDATE`.
  - **1b, checkout:** everything else. Done when the original Phase 1 check passes in the browser.

  Give each done-when a p99 latency line.
- **Connection budget.** PostgreSQL allows 100 connections by default (`max_connections`), and Npgsql pools up to 100 per data source (`Maximum Pool Size`). Under the k6 test, two services can take every slot. Then every health pool fails as well, which is the cascade ADR 0001 prevents. Fix it in one of two ways:
  - set an explicit pool size per service in `ServiceDefaults` (for example 20), or
  - raise `max_connections` in `compose.yaml`.

  Write the sum into ADR 0002, and have the k6 run watch `db.client.connection.*`.
- **Payments: module or service.** One option is a module inside Orders, with its own folder, `DbContext` and schema, extracted in Phase 7 together with Stripe. The other is a separate service from the start. The README marks this as open.
- **Cross-cutting decisions, one ADR each:**
  - retries and timeouts for synchronous calls (`Microsoft.Extensions.Http.Resilience`)
  - migrations applied by a job, not at startup
  - `ProblemDetails` and OpenAPI
  - service-to-service identity: forward the user's token, or use client credentials
  - Kafka: the partition key, JSON or a schema registry, a dead-letter topic, and draining on shutdown
  - which service owns video status
  - whether "creator" is a Keycloak role or a policy over the channel row
- **Keycloak issuer.** The browser gets tokens from `http://localhost:8180/realms/newstore`, but containers reach Keycloak at `http://keycloak:8080`. Decide `KC_HOSTNAME`, or validate the issuer explicitly, together with the realm import under `deploy/compose/keycloak/`.
- **Backlog.**
  - V-4 (#2) has no acceptance criteria, and E-4 (#20) and D-1 (#15) have thin ones.
  - The two comparisons have no cards: the last-copy approaches tested with k6, and the hand-written outbox.

## First PRs of Phase 1: hygiene

- **`.editorconfig`.** Do this in one PR, with one `dotnet format` commit:
  - Set `insert_final_newline = true` for C#.
  - Add `charset`, `end_of_line = lf` and `trim_trailing_whitespace` under `[*]`.
  - Change `type_parameters.applicable_kinds = namespace` to `type_parameter`.
- **Build settings, in a PR of their own, because they may surface warnings:**
  - `AnalysisLevel` `latest-recommended`
  - `CentralPackageTransitivePinningEnabled`
  - `ContinuousIntegrationBuild=true` in CI
- **Dependabot:**
  - Add the `dotnet-sdk` and `docker-compose` ecosystems, with directory `/`.
  - Change the `docker` entry to `directories: ["/src/Services/*"]`.
- **Dockerfiles** copy `src/Shared/` and their own service, not all of `src/`. `.dockerignore` adds `.superpowers/` and `**/*.md`.

## With the work that needs them

- **A heartbeat health check, added with the outbox relay,** the first background loop. Kafka stays out of `/health/ready`.
- **A shared test-support project, `tests/Shared/NewStore.TestSupport`,** set up before a second service copies `CatalogAppFactory`. It holds:
  - `SpanSink` and a generic app factory
  - span isolation per factory: each factory tags its requests, and the sink filters on the tag
  - a collector-down test that forces a flush, so it proves the export failed (check what `ForceFlush` returns on a failed export first)
  - per-test database isolation, once tests write rows
- **Kafka topics are created on purpose,** because auto-create is off. An idempotent step creates them with a partition count per key, and the partition key gets an ADR.
- **The gateway doesn't route `/health/*` from the internet.**
- **EF Core uses the request `NpgsqlDataSource`** (`UseNpgsql(dataSource)`), so the Npgsql traces and pool metrics describe the pool EF uses.
- **`docs/getting-started.md`** is specific to Phase 0. Give it a Phase 1 section, or make it generic.
- **Kafka's memory:** watch it during the first k6 run. If the container is killed for running out of memory, replace the `kafka-broker-api-versions.sh` health check.

## Later

- **`persist-credentials: false` on checkout,** in Phase 6, when a job gets write permissions or pushes to ECR.
- **EKS session arithmetic, in README Constraints.** A cluster takes 15–20 minutes to create and as long to destroy, so a useful session lasts about 90 minutes. The control plane costs about $0.10 an hour, plus the nodes. Size the $20 budget alert against that.
