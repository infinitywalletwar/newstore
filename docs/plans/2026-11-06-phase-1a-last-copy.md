# Phase 1a: Buy the Last Copy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. The `code-writer` agent (`~/.claude/agents/code-writer.md`) builds each task, and the main session reviews it before the owner sees the pull request. Tasks 1 and 2 run one after the other. Tasks 3, 4 and 5 are independent and run at the same time, each in its own git worktree and branch; see "Running three tasks at once". Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Inventory never sells more copies than an edition has, under 1,000 buyers at once, and a k6 test in CI proves it. The outbox and Kafka library and Catalog's browsing API are ready for Phase 1b.

**Architecture:** Inventory is the second service: editions, reservations and copies in its own PostgreSQL database through EF Core, with two interchangeable ways to take the last copy, a conditional `UPDATE` and an optimistic concurrency token, measured against each other and recorded in an ADR. `NewStore.Messaging` is a shared library: a transactional outbox with a relay, an idempotent Kafka consumer with a dead-letter topic, and topics created on purpose. No service uses it yet; 1b wires Inventory and Orders to it. Catalog gets its first data: channels and sample videos, browsing, full-text search and channel pages, cached with HybridCache on Redis. Migrations run as a one-shot job per service in Compose, never at startup. ServiceDefaults grows a database registration with a connection budget, ProblemDetails and OpenAPI. `tests/Shared/NewStore.TestSupport` replaces copies of the Catalog test factory.

**Tech Stack:** as Phase 0, plus EF Core 10.0.14 with Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 and EFCore.NamingConventions, Confluent.Kafka 2.16.0, HybridCache 10.11.0 with StackExchange.Redis, Microsoft.AspNetCore.OpenApi 10.0.14, Testcontainers.Kafka and Testcontainers.Redis 4.16.0, k6 1.8.2.

**Spec:** [`README.md`](../../README.md): Requirements E-1, E-3, C-1, C-3, C-4 and V-4, Architecture, the Phases table (rows 1a and 1b) and How we work. Also [`AGENTS.md`](../../AGENTS.md). The Phase 0 reviews' inputs are resolved in the next section, so `phase-1-inputs.md` is deleted by this plan's pull request.

All paths below are relative to the newstore repository root, `example/newstore/`.

## What the Phase 0 reviews asked

| Input | Where it goes |
|---|---|
| Split Phase 1 | Done. 1a is this plan. 1b (Orders, Payments, the gateway, sign-in, the Angular pages) gets its own plan. The README Phases table has both rows. |
| Connection budget | Task 2: `AddPostgresDatabase` sets `Maximum Pool Size=20`, the readiness pool keeps 2, `max_connections=200` in `compose.yaml`, and the sum goes into ADR 0002. Task 3's k6 run watches `db.client.connection.*`. |
| Payments: module or service | The 1b plan. Nothing in 1a depends on it. |
| Retries and timeouts for synchronous calls | The 1b plan, with the first synchronous call (Orders to Inventory). |
| Migrations applied by a job | Task 2 decides it (ADR 0005) and builds the `migrate` command. Tasks 3 and 5 use it. |
| ProblemDetails and OpenAPI | Task 2 (ADR 0006). |
| Service-to-service identity | The 1b plan. |
| Kafka: partition key, JSON or a registry, dead letters, draining | Task 4 (ADR 0008). |
| Which service owns video status | The Phase 2 plan. 1a's Catalog holds only published videos. |
| Creator: a role or a policy | The 1b plan. |
| Keycloak issuer | The 1b plan. |
| V-4 acceptance criteria | This pull request adds them to the README and to issue #2. |
| E-4 and D-1 criteria | The 1b and Phase 3 plans. |
| The two comparison cards | Tasks 3 and 4 are those cards. |
| Hygiene | Task 1. |
| A heartbeat health check with the first background loop | Task 4 (the outbox relay). Task 3's expiry sweeper is a loop too; it follows the same pattern. |
| Shared test support | Task 2. |
| Kafka topics created on purpose | Task 4. |
| The gateway doesn't route `/health/*` | The 1b plan. |
| EF Core uses the request `NpgsqlDataSource` | Task 2's `AddPostgresDatabase<TContext>` does `UseNpgsql(dataSource)`. |
| `docs/getting-started.md` | Made generic in this pull request. |
| Kafka's memory under k6 | Task 3 records `docker stats` during the run. |
| `persist-credentials`, EKS arithmetic | The Phase 6 plan. |

## Global Constraints

`AGENTS.md` binds every step: git and the board, code, tests and the local stack. This section adds what is specific to Phase 1a.

- Package versions, added to `Directory.Packages.props` as tasks need them:
  - Microsoft.EntityFrameworkCore and Microsoft.EntityFrameworkCore.Design `10.0.14`
  - Npgsql.EntityFrameworkCore.PostgreSQL `10.0.3`
  - EFCore.NamingConventions `10.0.1`
  - Microsoft.AspNetCore.OpenApi `10.0.14`
  - Confluent.Kafka `2.16.0`
  - Microsoft.Extensions.Caching.Hybrid `10.11.0`
  - Microsoft.Extensions.Caching.StackExchangeRedis `10.0.14`
  - Microsoft.Extensions.TimeProvider.Testing `10.11.0`
  - Testcontainers.Kafka and Testcontainers.Redis `4.16.0`
  - dotnet-ef `10.0.14`, as a local tool in `.config/dotnet-tools.json`
- Images: `grafana/k6:1.8.2` (it has an arm64 build). The Phase 0 tags stay.
- Ports: Inventory is 8102 in Compose and 5102 with `dotnet run`.
- ADR numbers are assigned here, so parallel tasks can't collide: 0005 and 0006 in Task 2, 0007 in Task 3, 0008 in Task 4, 0009 in Task 5.
- PostgreSQL identifiers are snake_case through `UseSnakeCaseNamingConvention()`, so SQL in migrations and tests reads like SQL.
- IDs are `Guid`s from `Guid.CreateVersion7()`: time-ordered, so B-tree inserts append.
- Enums are stored as strings (`HasConversion<string>()`), so a `psql` session can read them.
- Money is `decimal` mapped to `numeric(10,2)`. No currency in 1a: the README calls it test revenue.
- Timestamps are `DateTimeOffset` in `timestamp with time zone`. Every "now" comes from `TimeProvider` in DI, so tests move time instead of sleeping.
- Every `dotnet ef` command runs with `ASPNETCORE_ENVIRONMENT=Development`, so the design-time host finds the connection string in `appsettings.Development.json`.

## Running three tasks at once

- Tasks 3, 4 and 5 start together after Task 2 merges, each from the same `main`, each in its own worktree and branch: `phase-1/inventory`, `phase-1/messaging` and `phase-1/catalog-reads`.
- They share only these files, and only by adding lines: `NewStore.slnx`, `Directory.Packages.props`, `compose.yaml`, `.github/workflows/ci.yml`, `AGENTS.md` (the port table and Commands) and the README's Repository layout. Each task adds exactly what its Files list says to those files and changes nothing else in them.
- After the first of the three merges, the other two run `git merge origin/main` on their branch, resolve the additive conflicts, run the build and the tests, and push the merge. No rebase and no force-push, so the pull request's history stays reviewable.
- Only Task 3 runs `docker compose` on the Mac while the three run: the Compose project name and the host ports are shared, so two stacks can't coexist. Tasks 4 and 5 verify through Testcontainers, and CI's `compose-smoke` verifies their Compose changes.
- Three Testcontainers runs share Docker. PostgreSQL costs about 100 MB per container, Kafka about 1 GB, Redis a few MB, and the stack about 4 GB. That fits.

## Review Focus

1. **The same buyer reserves twice at once:** one request gets 201 and the other 409 `already-holding`; the partial unique index decides, not application code, so two instances of Inventory agree too. Test: `Same_buyer_cannot_hold_two_reservations_at_once` (Task 3).
2. **Confirm after the reservation expired but before the sweeper ran:** 409 `reservation-expired`, no copy number, and the copy returns to sale. Test: `Confirm_after_expiry_fails_and_returns_the_copy` (Task 3).
3. **Confirm twice**, as a retried payment callback will: the same copy number both times and one copy row. Test: `Confirm_twice_returns_the_same_copy_number` (Task 3).
4. **The business transaction rolls back:** nothing reaches Kafka. When it commits, each message reaches Kafka once, even with two relays. Tests: `Rolled_back_transaction_publishes_nothing` and `Two_relays_publish_each_message_once` (Task 4).
5. **Redis is down:** Catalog still answers reads within 2 seconds, and `/health/ready` says `Degraded` with 200, because a cache is not a dependency. Test: `Reads_still_work_when_redis_is_down` (Task 5).

## File Structure

```
newstore/
├── .config/dotnet-tools.json                  dotnet-ef
├── compose.yaml                               + max_connections, catalog-migrate, inventory, inventory-migrate
├── compose.load.yaml                          the k6 overlay: short reservation TTL and the k6 service
├── deploy/compose/postgres/init.sql           + the inventory role and database
├── src/Shared/ServiceDefaults/
│   ├── ServiceDefaultsExtensions.cs           + ProblemDetails, OpenAPI, NewStore.* sources and meters
│   └── Data/PostgresExtensions.cs             AddPostgresDatabase<TContext>, RunDatabaseCommandsAsync, IDatabaseSeeder
├── src/Shared/Messaging/                      NewStore.Messaging (Task 4)
│   ├── MessagingExtensions.cs  MessagingOptions.cs  MessagingBuilder.cs
│   ├── IIntegrationEvent.cs  IIntegrationEventHandler.cs
│   ├── Outbox/     OutboxMessage.cs  Outbox.cs  OutboxRelay.cs  RelayHeartbeat.cs  OutboxRelayHealthCheck.cs
│   ├── Inbox/      InboxMessage.cs  IdempotentDispatcher.cs
│   ├── Kafka/      KafkaProducer.cs  KafkaConsumerService.cs  TopicCreator.cs  MessageHeaders.cs
│   ├── MessagingModelBuilderExtensions.cs     modelBuilder.AddOutbox(), AddInbox()
│   └── Telemetry/MessagingTelemetry.cs        ActivitySource "NewStore.Messaging"
├── src/Services/Catalog/
│   ├── CatalogDbContext.cs  Migrations/        Task 2 creates them empty; Task 5 adds the tables
│   ├── Channels/   Channel.cs  ChannelEndpoints.cs
│   ├── Videos/     Video.cs  VideoEndpoints.cs  VideoQueries.cs
│   ├── Caching/    RedisHealthCheck.cs
│   └── Seed/       CatalogSeeder.cs  sample-catalog.json
├── src/Services/Inventory/                    (Task 3)
│   ├── NewStore.Inventory.csproj  Program.cs  appsettings*.json  Properties/launchSettings.json  Dockerfile
│   ├── InventoryDbContext.cs  InventoryOptions.cs  Migrations/
│   ├── Editions/       Edition.cs  EditionEndpoints.cs  EditionContracts.cs
│   ├── Reservations/   Reservation.cs  Copy.cs  ReservationEndpoints.cs  ReservationContracts.cs
│   │                   ReservationService.cs  IReserveStrategy.cs  ConditionalUpdateStrategy.cs
│   │                   ConcurrencyTokenStrategy.cs  ReservationExpiryService.cs
│   └── Telemetry/InventoryMetrics.cs
├── tests/Shared/NewStore.TestSupport/         (Task 2)
│   ├── NewStoreAppFactory.cs  SpanSink.cs  PostgresServer.cs  OtlpExportListener.cs
├── tests/Catalog.Tests/                       uses TestSupport from Task 2; Task 5 adds VideoTests, ChannelTests, CachingTests
├── tests/Inventory.Tests/                     (Task 3)
├── tests/Messaging.Tests/                     (Task 4)
├── tests/load/last-copy.js  tests/load/README.md   (Task 3)
└── docs/adr/0005… 0006… 0007… 0008… 0009…
```

---

### Task 1: Hygiene

One pull request, five commits, each reviewable alone. Branch `phase-1/hygiene`. Issue #34.

**Files:**
- Modify: `.editorconfig`, every `.cs` file (the format commit), `Directory.Build.props`, `Directory.Packages.props`, `.github/dependabot.yml`, `src/Services/Catalog/Dockerfile`, `.dockerignore`.

- [ ] **Step 1: `.editorconfig`**

Under `[*]` add `charset = utf-8`, `end_of_line = lf`, `trim_trailing_whitespace = true` and `insert_final_newline = true`. Delete `insert_final_newline = false` under `[*.cs]`. Change `dotnet_naming_symbols.type_parameters.applicable_kinds = namespace` to `type_parameter`. Commit: `style: tighten .editorconfig`.

- [ ] **Step 2: The format commit**

Run `dotnet format NewStore.slnx`. Commit the result with no hand edits in it: `style: apply dotnet format`. Then `dotnet format NewStore.slnx --verify-no-changes` exits 0.

- [ ] **Step 3: Build settings**

1. `Directory.Build.props`: add `<AnalysisLevel>latest-recommended</AnalysisLevel>` and `<ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>`.
2. `Directory.Packages.props`: add `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` to its property group.
3. `dotnet build NewStore.slnx`. Warnings are errors, so the new analyzers may fail the build. Fix each finding in the code when the fix is local. When a rule doesn't fit this code base, set `dotnet_diagnostic.<rule>.severity = none` in `.editorconfig` with a one-line comment saying why. If more than ten distinct rules fire, stop and report NEEDS_CONTEXT with the list.
4. `dotnet test --solution NewStore.slnx` passes. Commit: `build: raise the analysis level and pin transitive packages`.

- [ ] **Step 4: Dependabot**

Add ecosystems `dotnet-sdk` and `docker-compose`, both with `directory: "/"` and a weekly schedule. Change the `docker` entry's `directory: "/src/Services/Catalog"` to `directories: ["/src/Services/*"]`. Commit: `ci: widen Dependabot to the SDK, Compose and every service`.

- [ ] **Step 5: Docker build context**

1. In the Dockerfile, replace `COPY src/ src/` with `COPY src/Shared/ src/Shared/` and `COPY src/Services/Catalog/ src/Services/Catalog/`.
2. `.dockerignore`: add `.superpowers/` and `**/*.md`.
3. `docker compose up -d --wait` builds and starts; `curl http://localhost:8101/health/ready` prints `Healthy`; `docker compose down`.
4. Commit: `build(docker): copy only the shared and the service sources`.

- [ ] **Step 6: Pull request**

Walkthrough: one paragraph per commit. Interview angle: "Why pin transitive packages?" (a transitive bump can change behaviour without any direct reference changing; pinning makes every version an explicit, reviewed line). `Closes #34`.

---

### Task 2: Shared groundwork

Everything the three parallel tasks would otherwise each build. Branch `phase-1/groundwork`. Issue #35.

**Files:**
- Create `tests/Shared/NewStore.TestSupport/`:
  - `NewStore.TestSupport.csproj`: `Microsoft.NET.Sdk`, references `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `OpenTelemetry.Exporter.InMemory`, `Npgsql`, `Microsoft.EntityFrameworkCore`, and `<Using Include="Xunit" />`.
  - `NewStoreAppFactory.cs`, `SpanSink.cs` (moved from Catalog.Tests), `PostgresServer.cs`, `OtlpExportListener.cs`.
- Create `.config/dotnet-tools.json` with `dotnet-ef`.
- Create `src/Shared/ServiceDefaults/Data/PostgresExtensions.cs`.
- Create `src/Services/Catalog/CatalogDbContext.cs` (no entities yet) and its `Migrations/` with one empty `Initial` migration.
- Create `docs/adr/0005-migrations-run-as-a-job.md` and `docs/adr/0006-problem-details-and-openapi.md`.
- Modify: `src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj` (add Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL, EFCore.NamingConventions, Microsoft.AspNetCore.OpenApi), `ServiceDefaultsExtensions.cs`, `src/Services/Catalog/Program.cs`, `src/Services/Catalog/NewStore.Catalog.csproj` (add Microsoft.EntityFrameworkCore.Design with `PrivateAssets="all"`), `tests/Catalog.Tests/*` (use TestSupport), `compose.yaml`, `Directory.Packages.props`, `NewStore.slnx`, `docs/adr/0002-…md`, `AGENTS.md`, `README.md` (layout).

**Interfaces:**
- Produces, in `NewStore.ServiceDefaults.Data`. Tasks 3 and 5 build on these.

  ```csharp
  public static class PostgresExtensions
  {
      // Reads ConnectionStrings:<connectionName>; throws InvalidOperationException naming the key when it is
      // missing or empty. Registers one NpgsqlDataSource with Maximum Pool Size = 20 unless the connection
      // string sets its own, the "postgres" readiness check on its separate 2-connection pool (Phase 0's
      // AddPostgres), and TContext with UseNpgsql(dataSource) and UseSnakeCaseNamingConvention().
      public static IHostApplicationBuilder AddPostgresDatabase<TContext>(
          this IHostApplicationBuilder builder, string connectionName) where TContext : DbContext;

      // Handles the service's command line. Returns null when args is empty: the host should run. Otherwise
      // runs each argument in order and returns the exit code: "migrate" applies pending migrations;
      // "seed" calls IDatabaseSeeder<TContext> from DI (0 and a log line when none is registered).
      // An unknown argument logs it and returns 2. The host never starts.
      public static Task<int?> RunDatabaseCommandsAsync<TContext>(this WebApplication app, string[] args)
          where TContext : DbContext;
  }

  // The seeder decides whether its data is already there, so "seed" can run every start.
  public interface IDatabaseSeeder<in TContext> where TContext : DbContext
  {
      Task SeedAsync(TContext db, CancellationToken cancellationToken);
  }
  ```

  A service's `Program.cs` takes this shape; Tasks 3 and 5 copy it:

  ```csharp
  var builder = WebApplication.CreateBuilder(args);
  builder.AddServiceDefaults();
  builder.AddPostgresDatabase<CatalogDbContext>("Catalog");
  var app = builder.Build();
  if (await app.RunDatabaseCommandsAsync<CatalogDbContext>(args) is { } exitCode)
  {
      return exitCode;
  }
  app.MapDefaultEndpoints();
  app.Run();
  return 0;
  ```

  `WebApplication.CreateBuilder(args)` ignores arguments that don't start with `--`, `-` or `/`, so `migrate` reaches `args` untouched. Verify this on the first run and note it in the report.

- Produces, in `NewStore.ServiceDefaults`: `AddServiceDefaults` also calls `AddProblemDetails()` and `AddOpenApi()`, adds the tracing source `NewStore.*` and the meter `NewStore.*` (wildcards), so a library's own `ActivitySource` and `Meter` named `NewStore.<Something>` are collected without touching ServiceDefaults again. `MapDefaultEndpoints` also adds `UseExceptionHandler()` and `UseStatusCodePages()` before the health endpoints and maps `MapOpenApi()` (`/openapi/v1.json`) in every environment; services call it before any other middleware. Errors are `application/problem+json` (RFC 9457).

- Produces, in `NewStore.TestSupport`. Every test project uses these.

  ```csharp
  // Starts TProgram in the Testing environment with scope and build validation on, applies Settings with
  // UseSetting, and attaches an in-memory exporter. Isolation: TraceIdPrefix is 8 random hex characters;
  // ConfigureClient gives every client a traceparent header 00-<TraceIdPrefix><24 hex>-<16 hex>-01, and
  // ExportedSpans keeps only spans whose TraceId starts with TraceIdPrefix. So two factories in one process
  // never see each other's spans, although ActivitySource listeners are process-wide.
  public class NewStoreAppFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
  {
      public Dictionary<string, string?> Settings { get; }
      public string TraceIdPrefix { get; }
      public SpanSink ExportedSpans { get; }
      // Polls ExportedSpans every 50 ms until a span matches; fails the test after timeout (default 5 s).
      public Task<Activity> WaitForSpanAsync(Func<Activity, bool> match, TimeSpan? timeout = null);
      // Applies TContext's migrations to the configured database. Call it from a derived factory's InitializeAsync.
      public Task MigrateAsync<TContext>() where TContext : DbContext;
  }

  // Phase 0's SpanSink, plus the prefix filter: Add ignores spans whose TraceId doesn't start with the prefix.
  public sealed class SpanSink(string traceIdPrefix) : ICollection<Activity> { public Activity[] Snapshot(); }

  // One PostgreSQL container per test assembly (register it with [assembly: AssemblyFixture]).
  // CreateDatabaseAsync runs CREATE DATABASE "<name>_<n>" on the container and returns that database's
  // connection string, so each test class gets its own database and tests that write rows don't collide.
  public sealed class PostgresServer : IAsyncLifetime
  {
      public Task<string> CreateDatabaseAsync(string name, CancellationToken cancellationToken);
  }

  // Listens to the OTLP exporter's EventSource and records its export failures, so a collector-down test
  // can prove that an export was attempted and failed, not just that nothing crashed.
  public sealed class OtlpExportListener : EventListener { public int Failures { get; } }
  ```

  Class fixtures receive the assembly fixture through their constructor: xUnit v3 injects assembly fixtures into class fixtures. Check this against the xUnit v3 docs before relying on it; if it isn't supported, make `PostgresServer` a collection fixture for the whole assembly instead and say so in the report.

- [ ] **Step 1: Test support project**

1. Create the project and move `SpanSink` into it with the prefix filter. Write `NewStoreAppFactory<TProgram>`, `PostgresServer` and `OtlpExportListener`.
2. Convert `tests/Catalog.Tests`: `PostgresCatalogAppFactory` becomes a class fixture that takes `PostgresServer`, creates its database and sets `ConnectionStrings:Catalog`; `CatalogAppFactory` goes away in favour of `NewStoreAppFactory<Program>`. `TracingTests` drop their unique-path workaround, since spans are isolated now; they use `WaitForSpanAsync`.
3. First find out what `TracerProvider.ForceFlush` returns when the OTLP endpoint refuses connections: write a throwaway test, run it, and record the result in the report. Then `Service_keeps_serving_when_collector_is_unreachable` gains: force a flush, then assert `OtlpExportListener.Failures > 0`. If `ForceFlush` already returns `false` on a failed export, assert that too.
4. `dotnet test --project tests/Catalog.Tests`: all pass, with the span-isolation change visible in the diff. Commit: `test: share the app factory, span sink and database server`.

- [ ] **Step 2: Database registration and commands**

1. Add the packages to `Directory.Packages.props` and the tool manifest (`dotnet new tool-manifest`, `dotnet tool install dotnet-ef --version 10.0.14`).
2. Write `PostgresExtensions`. `AddPostgresDatabase<TContext>` keeps Phase 0's fail-fast and health behaviour, so the existing Catalog tests keep passing unchanged.
3. Create `CatalogDbContext` with no entities. Run `ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add Initial --project src/Services/Catalog`. The migration is empty apart from the history table. Catalog's `Program.cs` takes the shape above.
4. Tests in `tests/Catalog.Tests/DatabaseCommandTests.cs`, against a fresh database from `PostgresServer`. `WebApplicationFactory` can't pass arguments, so test the extension directly: build a `WebApplication` in the test with the same registrations, call `RunDatabaseCommandsAsync<CatalogDbContext>(["migrate"])`, and assert:
   - `Migrate_creates_the_migrations_history_and_returns_0`: result is 0, and `SELECT count(*) FROM "__EFMigrationsHistory"` is 1.
   - `Seed_without_a_seeder_returns_0`.
   - `Unknown_command_returns_2`.
   - `No_arguments_returns_null`.
5. Commit: `feat(service-defaults): register databases with a pool budget and run migrations as a command`.

- [ ] **Step 3: ProblemDetails and OpenAPI**

1. Tests in `tests/Catalog.Tests/ProblemDetailsTests.cs`:
   - `Unknown_path_returns_a_404_problem`: `GET /no-such-page` is 404 with `Content-Type: application/problem+json` and a body whose `status` is 404.
   - `OpenApi_document_is_served`: `GET /openapi/v1.json` is 200 and the JSON has `openapi` and `paths`.
2. Implement in `ServiceDefaultsExtensions`, plus the `NewStore.*` source and meter. Run the whole Catalog suite: the health tests still see `Healthy`/`Unhealthy` plain text, because the health writer sets the body before status code pages run.
3. Write ADR 0006: ProblemDetails for every error response, OpenAPI mapped in every environment (the gateway won't expose it), the main alternative (hand-written error envelopes) and its cost.
4. Commit: `feat(service-defaults): problem details and OpenAPI for every service`.

- [ ] **Step 4: Compose and the connection budget**

1. `compose.yaml`: `postgres` gets `command: ["postgres", "-c", "max_connections=200"]`. Add `catalog-migrate`: same `build`, `image` and `pull_policy` as `catalog`, `command: ["migrate"]`, `restart: "no"`, the same environment, `depends_on: postgres: service_healthy`. `catalog` gets `depends_on: catalog-migrate: { condition: service_completed_successfully }`.
2. `docker compose up -d --wait`. If `--wait` fails because `catalog-migrate` exited, record the exact message and instead run `docker compose run --rm catalog-migrate` followed by `docker compose up -d --wait`; put whichever works into `AGENTS.md` Commands and into CI's `compose-smoke`. `curl http://localhost:8101/health/ready` prints `Healthy`. `docker compose exec postgres psql -U catalog -d catalog -c '\dt'` shows `__EFMigrationsHistory`. `docker compose down -v` (the volume, because `init.sql` is unchanged but the budget must be checked on a fresh server: `SHOW max_connections` prints 200).
3. ADR 0002, Consequences: add the budget. Each service's request pool holds 20 and its readiness pool 2; the six services with a database need 132; `max_connections=200` leaves room for migration jobs and `psql`. Task 3's k6 run confirms the pending-request and timeout metrics stay at 0 timeouts.
4. Write ADR 0005: migrations run as a one-shot job (`migrate`) before the service starts, never inside the service's startup; alternatives: migrate at startup (every replica races, and a failed migration takes the service down with it), a separate migrations project (another project to keep in sync).
5. Commit: `feat(compose): migration job for Catalog and a connection budget`.

- [ ] **Step 5: Docs and the pull request**

1. `AGENTS.md`: in Code, the `Program.cs` shape and the `migrate`/`seed` contract; in Tests, "use `NewStore.TestSupport`; never copy a factory"; in Commands, `dotnet ef` with the environment variable, `docker compose run --rm catalog-migrate` if Step 4 needed it. README layout gets `tests/Shared/` and `.config/`.
2. CI: `dotnet build` already builds TestSupport through the solution. `compose-smoke` unchanged unless Step 4 says otherwise.
3. Walkthrough: file by file; interview angles: "Why not run migrations at startup?", "Why is Redis or Kafka not in the readiness check?" (preview of Tasks 4 and 5), "How do you size a connection pool?". `Closes #35`.

---

### Task 3: Inventory, the last copy

The hard part `AGENTS.md` calls "reservations and copy numbers". Branch `phase-1/inventory`, worktree. Issue #36. Advances C-1 (#6), C-3 (#8), C-4 (#9), E-1 (#3) and E-3 (#5); those stories close in 1b, when a buyer can do this in the browser.

**Files:**
- Create `src/Services/Inventory/` as in File Structure. The csproj is `Microsoft.NET.Sdk.Web`, references ServiceDefaults and `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets="all"`). `launchSettings.json` `http` profile: `http://localhost:5102`, `OTEL_EXPORTER_OTLP_ENDPOINT` `http://localhost:4317`, `OTEL_SERVICE_NAME` `inventory`. `appsettings.Development.json`: `ConnectionStrings:Inventory` = `Host=localhost;Port=5432;Database=inventory;Username=inventory;Password=inventory`. Dockerfile: Catalog's, with Inventory's paths.
- Create `tests/Inventory.Tests/` (`NewStore.Inventory.Tests.csproj` references TestSupport, the Inventory project, `Microsoft.Extensions.TimeProvider.Testing`), `tests/load/last-copy.js`, `tests/load/README.md`, `compose.load.yaml`, `docs/adr/0007-the-last-copy.md`.
- Modify, by adding lines only: `NewStore.slnx`, `Directory.Packages.props` (Microsoft.Extensions.TimeProvider.Testing), `compose.yaml` (`inventory`, `inventory-migrate`), `deploy/compose/postgres/init.sql` (three lines for `inventory`), `.github/workflows/ci.yml` (the Inventory curl and the k6 step in `compose-smoke`), `AGENTS.md` (port table, the k6 command), `README.md` layout (`tests/load/`, `compose.load.yaml`).

**Interfaces:**
- Entities, in `NewStore.Inventory`:

  ```csharp
  public enum EditionFormat { Digital, Dvd }
  public enum ReservationStatus { Pending, Confirmed, Expired, Released }

  public sealed class Edition
  {
      public Guid Id { get; init; }
      public Guid VideoId { get; init; }
      public Guid CreatorId { get; init; }
      public EditionFormat Format { get; init; }
      public decimal Price { get; init; }
      public int CopiesTotal { get; init; }
      public int CopiesLeft { get; set; }        // down on reserve, up on release and expiry
      public int CopiesAssigned { get; set; }    // the last copy number handed out; only confirm moves it
      public DateTimeOffset CreatedAt { get; init; }
      public uint Version { get; }               // mapped to PostgreSQL's xmin system column as the concurrency token
  }

  public sealed class Reservation
  {
      public Guid Id { get; init; }
      public Guid EditionId { get; init; }
      public Guid BuyerId { get; init; }
      public ReservationStatus Status { get; set; }
      public DateTimeOffset ReservedAt { get; init; }
      public DateTimeOffset ExpiresAt { get; init; }   // ReservedAt + InventoryOptions.ReservationTtl
  }

  public sealed class Copy
  {
      public Guid Id { get; init; }
      public Guid EditionId { get; init; }
      public int Number { get; init; }
      public Guid OwnerId { get; init; }
      public Guid ReservationId { get; init; }
      public DateTimeOffset AssignedAt { get; init; }
  }
  ```

  Indexes in the model: `reservations (edition_id, buyer_id)` unique with filter `status IN ('Pending', 'Confirmed')` (C-1's one copy per buyer; the database enforces it); `reservations (status, expires_at)` for the sweeper; `copies (edition_id, number)` unique (C-3); `copies (reservation_id)` unique (one copy per reservation). Check constraint `copies_total BETWEEN 1 AND 10000`.

- Options, bound from `Inventory:*`:

  ```csharp
  public sealed class InventoryOptions
  {
      public TimeSpan ReservationTtl { get; set; } = TimeSpan.FromMinutes(10);
      public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(5);
      public ReserveStrategy ReserveStrategy { get; set; } = ReserveStrategy.ConditionalUpdate;
      public int MaxReserveAttempts { get; set; } = 20;   // ConcurrencyToken only
  }
  public enum ReserveStrategy { ConditionalUpdate, ConcurrencyToken }
  ```

- The strategies, both registered; `IReserveStrategy` resolves to the configured one:

  ```csharp
  public interface IReserveStrategy
  {
      // Takes one copy of the edition for the buyer in its own transaction, or says why not.
      Task<ReserveOutcome> ReserveAsync(Guid editionId, Guid buyerId, CancellationToken cancellationToken);
  }

  public abstract record ReserveOutcome
  {
      public sealed record Reserved(Reservation Reservation) : ReserveOutcome;
      public sealed record SoldOut : ReserveOutcome;
      public sealed record AlreadyHolding : ReserveOutcome;   // a Pending or Confirmed reservation exists for this buyer
      public sealed record NotFound : ReserveOutcome;
      public sealed record Contended(int Attempts) : ReserveOutcome;   // ConcurrencyToken gave up after MaxReserveAttempts
  }
  ```

  - `ConditionalUpdateStrategy`: in a transaction, `ExecuteUpdateAsync` on `Editions` where `Id == editionId && CopiesLeft > 0`, setting `CopiesLeft - 1`. Zero rows: `SoldOut` if the edition exists, else `NotFound`. Then add the `Reservation` and `SaveChangesAsync`. A `DbUpdateException` whose inner `PostgresException.SqlState` is `23505` (the partial unique index) means `AlreadyHolding`; the rollback also undoes the decrement. No retries: the database serialises the decrement.
  - `ConcurrencyTokenStrategy`: up to `MaxReserveAttempts` times: load the edition (tracked), `NotFound` if null, `SoldOut` if `CopiesLeft == 0`, else decrement in memory, add the reservation, `SaveChangesAsync`. `DbUpdateConcurrencyException` (xmin changed) clears the change tracker and tries again. The same `23505` handling. After the last attempt, `Contended(attempts)`.
  - `InventoryMetrics` (`Meter("NewStore.Inventory")`): counter `inventory.reservations` with tag `outcome`, histogram `inventory.reserve.attempts`.

- `ReservationService` (the rest; one method per endpoint). Confirm and release lock the reservation row with `FromSql($"SELECT * FROM reservations WHERE id = {id} FOR UPDATE")` inside a transaction, so two confirms of one reservation run one after the other:
  - **Confirm:** `Confirmed` already: return it with its copy number (idempotent). `Released`, `Expired`: 409. `Pending` and `ExpiresAt <= now`: set `Expired`, `CopiesLeft + 1`, commit, 409 `reservation-expired`. Else `UPDATE editions SET copies_assigned = copies_assigned + 1 WHERE id = {editionId} RETURNING copies_assigned` (`SqlQuery<int>`), add the `Copy` with that number, set `Confirmed`, commit. Numbers follow commit order, and a rolled-back confirm takes no number, so there are no gaps.
  - **Release:** `Pending`: `Released` and `CopiesLeft + 1`, 204. `Released` or `Expired`: 204 (idempotent). `Confirmed`: 409 `reservation-confirmed`.
  - **Sweep** (`ReservationExpiryService`, a `BackgroundService` on a `PeriodicTimer(ExpirySweepInterval, timeProvider)`): one statement, so it is atomic and works with several instances:

    ```sql
    WITH expired AS (
        UPDATE reservations SET status = 'Expired'
        WHERE status = 'Pending' AND expires_at <= {now}
        RETURNING edition_id)
    UPDATE editions e SET copies_left = e.copies_left + x.n
    FROM (SELECT edition_id, count(*) AS n FROM expired GROUP BY edition_id) x
    WHERE e.id = x.edition_id
    ```

    It logs how many it expired when the count is above 0, and it stamps a heartbeat timestamp of its own after each loop; a `"reservation-sweeper"` health check tagged `live` is `Unhealthy` when the last loop is older than `3 × ExpirySweepInterval + 5 s`. Task 4 builds the same pattern for the relay in parallel; the two move into ServiceDefaults when a third loop appears.

- Endpoints, JSON camelCase, errors as ProblemDetails with `type` `urn:newstore:problem:<slug>`:

  | Endpoint | Body | Replies |
  |---|---|---|
  | `POST /editions` | `{ videoId, creatorId, format, price, copies }` | 201, `Location: /editions/{id}`, `EditionResponse`. 400 validation problem: `copies` outside 1..10000, `price` < 0, empty IDs, unknown `format`. |
  | `GET /editions/{id}` | | 200 `EditionResponse`; 404. |
  | `GET /editions/{id}/copies` | | 200 `[{ number, ownerId, assignedAt }]` by number; 404. |
  | `POST /editions/{id}/reservations` | `{ buyerId }` | 201, `Location: /reservations/{rid}`, `ReservationResponse`; 404; 409 `sold-out`; 409 `already-holding`; 503 `contended` with `Retry-After: 1`. |
  | `GET /reservations/{id}` | | 200 `ReservationResponse`; 404. |
  | `POST /reservations/{id}/confirm` | | 200 `ReservationResponse` with `copyNumber`; 404; 409 `reservation-expired`; 409 `reservation-released`. |
  | `POST /reservations/{id}/release` | | 204; 404; 409 `reservation-confirmed`. |

  `EditionResponse { id, videoId, creatorId, format, price, copiesTotal, copiesLeft, copiesSold }` where `copiesSold` is `copiesAssigned`. `ReservationResponse { id, editionId, buyerId, status, reservedAt, expiresAt, copyNumber }` with `copyNumber` null until confirmed. In 1a the buyer is the `buyerId` in the body; 1b takes it from the token and removes the field.

- Tests run against **both** strategies: an abstract `ReservationTests` with the test methods, and `ReservationTests_ConditionalUpdate` and `ReservationTests_ConcurrencyToken`, each with a class fixture that sets `Inventory:ReserveStrategy`, creates its own database from `PostgresServer`, migrates, and replaces `TimeProvider` with a `FakeTimeProvider` (`ConfigureTestServices`). `Inventory:ExpirySweepInterval` is `00:00:01` in tests, and the sweeper's `PeriodicTimer` runs on the fake clock, so `Advance` triggers it.

- [ ] **Step 1: Project, model and migration**

1. Create the project, the test project, the entities, `InventoryDbContext` (snake_case through `AddPostgresDatabase`), the indexes and the check constraint. `ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add Initial --project src/Services/Inventory`. Read the generated migration: the partial index must carry its `WHERE` clause, and `editions` must not have a `version` column (xmin is a system column).
2. `init.sql`: the three lines for `inventory`. `compose.yaml`: `inventory` (port `127.0.0.1:8102:8080`, the same shape as `catalog`) and `inventory-migrate`.
3. `Program.cs` as in Task 2, plus options binding, the strategies, the sweeper and the endpoints' `Map` calls.
4. Test `Startup_fails_when_connection_string_is_missing` (the Catalog pattern) and `Ready_is_healthy_with_the_database`. Commit: `feat(inventory): project, model and first migration`.

- [ ] **Step 2: Editions (failing tests, then code)**

`EditionTests(InventoryFixture fixture)`:
- `Create_edition_returns_201_with_all_copies_left`: `copies` 10 → 201, `copiesLeft` 10, `copiesSold` 0, `Location` resolves to the same edition.
- `Create_edition_with_copies_outside_the_range_returns_400` (theory: 0 and 10001): 400 with `errors.copies`.
- `Unknown_edition_returns_404_problem`.
- `Copies_of_a_new_edition_is_an_empty_list`.

Commit: `feat(inventory): create and read editions`.

- [ ] **Step 3: Reserve (failing tests, then both strategies)**

In `ReservationTests`:
- `Reserve_takes_one_copy`: 201, `copiesLeft` 9, `status` Pending, `expiresAt - reservedAt` is 10 minutes.
- `Reserve_when_sold_out_returns_409_sold_out`: an edition with 1 copy, two buyers.
- `Same_buyer_cannot_hold_two_reservations_at_once`: 100 copies, 50 parallel reserves by one buyer (`Task.WhenAll`) → exactly one 201 and 49 × 409 `already-holding`; `copiesLeft` 99.
- `A_hundred_buyers_for_ten_copies_get_exactly_ten`: 100 parallel reserves, distinct buyers → 10 × 201, 90 × 409 `sold-out`, `copiesLeft` 0. With ConcurrencyToken, assert no 503.
- `Reserve_for_unknown_edition_returns_404`.

Commit: `feat(inventory): reserve a copy with two interchangeable strategies`.

- [ ] **Step 4: Confirm, release and expiry (failing tests, then code)**

- `Confirm_assigns_numbers_in_confirm_order_without_gaps`: reserve three, confirm the third, the first, the second → numbers 1, 2, 3 in that order; `GET /editions/{id}/copies` lists 1, 2, 3 with the right owners; `copiesSold` 3.
- `Confirm_twice_returns_the_same_copy_number`: and one row in copies.
- `Confirm_after_expiry_fails_and_returns_the_copy`: reserve, `Advance(10 min + 1 s)` without waiting for the sweeper → confirm is 409 `reservation-expired`, `copiesLeft` back, `GET /reservations/{id}` is Expired.
- `Sweeper_expires_pending_reservations_and_returns_their_copies`: reserve three on one edition and one on another, `Advance(11 min)` → within 2 s all four are Expired and both `copiesLeft` are back.
- `Expired_reservation_never_uses_a_number`: reserve A then B, expire A, confirm B → number 1.
- `Release_returns_the_copy_and_is_idempotent`: 204 twice, `copiesLeft` back; releasing a confirmed one is 409 `reservation-confirmed`.
- `Sweeper_heartbeat_is_live`: `/health/live` 200 while the sweeper runs.

Commit: `feat(inventory): confirm, release and expire reservations`.

- [ ] **Step 5: The k6 test**

1. `compose.load.yaml`, an overlay used with `-f compose.yaml -f compose.load.yaml`: `inventory` gets `Inventory__ReservationTtl: "00:00:03"`, `Inventory__ExpirySweepInterval: "00:00:01"` and `Inventory__ReserveStrategy: ${RESERVE_STRATEGY:-ConditionalUpdate}`; a `k6` service: `image: grafana/k6:1.8.2`, `profiles: [load]`, `volumes: ["./tests/load:/scripts:ro"]`, `command: ["run", "/scripts/last-copy.js", "-e", "BASE_URL=http://inventory:8080", "-e", "P99_MS=${P99_MS:-1000}"]`, `depends_on: inventory: service_healthy`, memory limit 512m.
2. `tests/load/last-copy.js`:
   - `setup()`: two editions with 10 copies each; returns their IDs.
   - Scenario `last_copy`, executor `per-vu-iterations`, 1000 VUs, 1 iteration, `maxDuration` 2m: a fresh `buyerId` per VU; reserve (request tag `name: reserve`); on 201 confirm twice (tag `name: confirm`) and check both replies carry the same `copyNumber`, counter `copies_confirmed`; on 409 `sold-out` counter `sold_out`; anything else fails a check.
   - Scenario `expiry`, 10 VUs, on the second edition: reserve, `sleep(6)` (TTL 3 s + sweep 1 s + margin), reserve again as a new buyer → 201, confirm → a number.
   - `teardown()`: `GET /editions/{first}/copies` has 10 entries whose numbers are exactly 1..10; `copiesLeft` 0. The second edition: the same.
   - `http.setResponseCallback(http.expectedStatuses(200, 201, 204, 409))`, so a 409 is not a failed request.
   - Thresholds: `checks: ['rate==1']`, `copies_confirmed: ['count==10']`, `sold_out: ['count==990']`, `http_req_failed: ['rate==0']`, `http_req_duration{name:reserve}: ['p(99)<' + P99_MS]`.
3. Run on the Mac: `docker compose -f compose.yaml -f compose.load.yaml up -d --wait`, then `docker compose -f compose.yaml -f compose.load.yaml run --rm k6`. Every threshold passes. During the run, `docker stats --no-stream` once: record Kafka's memory. Put the k6 summary in the report.
4. `tests/load/README.md`: how to run it, what the thresholds mean, and that the overlay shortens the TTL only for the test.
5. Commit: `test(load): k6 proves the last copy is sold once`.

- [ ] **Step 6: Measure both strategies, write ADR 0007**

1. Temporarily set the collector's metrics `debug` exporter to `verbosity: detailed` (don't commit it). For each strategy (`RESERVE_STRATEGY=ConditionalUpdate`, then `ConcurrencyToken`): `docker compose … down -v`, `up -d --wait`, run k6, then from `docker compose logs otel-collector` record the max of `db.client.connection.pending_requests`, the total of `db.client.connection.timeouts` (must be 0), and the `inventory.reserve.attempts` histogram (sum, count, max). From k6: `copies_confirmed`, `sold_out`, reserve p50 and p99, confirm p99.
2. ADR 0007: context (C-4), the decision (`ConditionalUpdate` is the default; `ConcurrencyToken` stays as the comparison and the switch), the table from step 1, alternatives (pessimistic `SELECT … FOR UPDATE` on the edition row, `SERIALIZABLE` isolation with retries, an advisory lock, a counter in Redis) and what each costs, consequences (one `UPDATE` per reserve, no retries, no gaps; the token approach's retries grow with contention).
3. Restore the collector config. Commit: `docs(adr): the last copy, measured`.

- [ ] **Step 7: CI, docs and the pull request**

1. `compose-smoke`: after the Catalog curl, `curl … http://localhost:8102/health/ready`; then "Run the last-copy load test": `docker compose -f compose.yaml -f compose.load.yaml run --rm k6` with `P99_MS=2000` in the step's `env` (the runner is slower than the Mac). Both compose commands in the job use both files.
2. `AGENTS.md`: the port rows (8102, 5102) and the k6 command. README layout.
3. Walkthrough: this is a hard part. Explain reserve, confirm and the sweeper step by step, show the test that proves each, and suggest one small change for the owner (for example: remove the partial index's `WHERE` and see which test fails and why). Interview angles: "How do you prevent overselling?", "Why does the copy number come from an `UPDATE … RETURNING` and not from `count(*) + 1`?", "What does `FOR UPDATE` cost under contention?". `Closes #36`.

---

### Task 4: Messaging: the outbox and Kafka

The hard parts `AGENTS.md` calls "the outbox" and "idempotent consumers". A library with tests; no service uses it in 1a. Branch `phase-1/messaging`, worktree. Issue #37.

**Files:**
- Create `src/Shared/Messaging/` as in File Structure. `NewStore.Messaging.csproj`: `Microsoft.NET.Sdk`, references `Confluent.Kafka`, `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL` and `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (for health checks and hosting).
- Create `tests/Messaging.Tests/`: references TestSupport, the library, `Testcontainers.Kafka`, `Microsoft.Extensions.TimeProvider.Testing`. It holds a test model: `Thing` entity, `ThingCreated : IIntegrationEvent`, `ThingsDbContext` with `AddOutbox()` and `AddInbox()`, and a `MessagingHost` helper that builds a `HostApplicationBuilder` with `AddMessaging<ThingsDbContext>` against the test's Kafka and database.
- Create `docs/adr/0008-kafka-events.md`.
- Modify, by adding lines only: `NewStore.slnx`, `Directory.Packages.props` (Confluent.Kafka, Testcontainers.Kafka), `AGENTS.md` (an "Events" paragraph under Architecture rules: how to publish and consume), README layout.

**Interfaces:**

```csharp
public interface IIntegrationEvent
{
    Guid EventId { get; }               // the message id, and the inbox key
    DateTimeOffset OccurredAt { get; }
}

public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    // Runs inside the inbox transaction on the consumer's DbContext, so its writes commit with the inbox row.
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}

public interface IOutbox
{
    // Adds an outbox row to the current TContext; the caller's SaveChangesAsync commits it with the business rows.
    void Enqueue<TEvent>(TEvent @event, string topic, string key) where TEvent : IIntegrationEvent;
}

public static class MessagingExtensions
{
    // Binds Messaging:* (BootstrapServers required, ClientId defaults to the service name), registers the producer,
    // the relay and its "outbox-relay" live check, the topic creator, the consumer and the inbox dispatcher.
    public static IHostApplicationBuilder AddMessaging<TContext>(
        this IHostApplicationBuilder builder, Action<MessagingBuilder> configure) where TContext : DbContext;
}

public sealed class MessagingBuilder
{
    public MessagingBuilder AddTopic(string name, int partitions);   // created at startup when missing
    public MessagingBuilder AddHandler<TEvent, THandler>(string topic)
        where TEvent : IIntegrationEvent where THandler : class, IIntegrationEventHandler<TEvent>;
}

public static class MessagingModelBuilderExtensions
{
    public static ModelBuilder AddOutbox(this ModelBuilder modelBuilder);   // table outbox_messages
    public static ModelBuilder AddInbox(this ModelBuilder modelBuilder);    // table inbox_messages
}

public sealed class MessagingOptions
{
    public required string BootstrapServers { get; set; }
    public string? ClientId { get; set; }
    public TimeSpan RelayInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int RelayBatchSize { get; set; } = 100;
    public TimeSpan ProduceTimeout { get; set; } = TimeSpan.FromSeconds(5);  // Confluent MessageTimeoutMs
    public int MaxHandlerAttempts { get; set; } = 3;
    public string DeadLetterSuffix { get; set; } = ".dlt";
}
```

- `outbox_messages`: `id` (uuid v7, the message id = `EventId`), `topic`, `key`, `type` (the CLR type's full name), `payload` (`jsonb`), `occurred_at`, `trace_parent` (the W3C header at enqueue time), `published_at` (null until sent), `attempts`, `last_error`. Index on `published_at` where null.
- `inbox_messages`: `consumer` (the group id), `message_id`, `handled_at`; primary key `(consumer, message_id)`.
- Kafka headers on every message: `message-id`, `type`, `occurred-at`, `traceparent`.
- **Relay** (`OutboxRelay : BackgroundService`): every `RelayInterval`, in a transaction, `SELECT … FROM outbox_messages WHERE published_at IS NULL ORDER BY id LIMIT {RelayBatchSize} FOR UPDATE SKIP LOCKED` (`FromSql`), produce the whole batch concurrently (`ProduceAsync` each, then await all), set `published_at` on the ones that succeeded, `attempts + 1` and `last_error` on the ones that failed, commit. `SKIP LOCKED` lets two relays share the table without sending twice. Producer: `Acks.All`, `EnableIdempotence = true`, `MessageTimeoutMs = ProduceTimeout`. A failed message is retried on the next loop, forever: Kafka being down must not lose events, so the relay never drops. Each loop ends by stamping `RelayHeartbeat.LastLoopAt` from `TimeProvider`; `OutboxRelayHealthCheck` ("outbox-relay", tagged `live`) is `Unhealthy` when that is older than `3 × RelayInterval + ProduceTimeout`. Kafka is in no readiness check: a service without Kafka still serves reads.
- **Consumer** (`KafkaConsumerService : BackgroundService`): one consumer per service, `GroupId` = `ClientId`, `EnableAutoCommit = false`, `EnableAutoOffsetStore = false`, subscribed to every topic with a handler. `Consume` blocks, so the loop runs on a long-running task. Per message: `IdempotentDispatcher` opens a scope and a transaction, `INSERT INTO inbox_messages … ON CONFLICT DO NOTHING`; zero rows means already handled, so skip; else deserialise by the `type` header, call the handler, commit; then `StoreOffset`, and `Commit` every 100 messages or 1 s. A handler exception retries in place with a 1 s wait up to `MaxHandlerAttempts`; then the raw message goes to `<topic><DeadLetterSuffix>` with headers `error`, `original-topic`, `original-partition`, `original-offset`, its offset is stored, and the loop moves on. On stop: finish the message in flight, `Commit()`, `Close()`.
- **Topics** (`TopicCreator : IHostedService`, registered before the consumer): `CreateTopicsAsync` for every `AddTopic` and for the dead-letter topic of every handled topic (1 partition); `TopicAlreadyExists` is fine; five attempts 2 s apart while the broker is starting.
- **Telemetry** (`ActivitySource("NewStore.Messaging")`): producer span `publish <topic>` (kind Producer) whose parent is the stored `trace_parent`, so a request's spans and its event's span share one trace; consumer span `process <topic>` (kind Consumer) with the parent from the header. Attributes `messaging.system = kafka`, `messaging.destination.name`, `messaging.message.id`.

- [ ] **Step 1: Model, outbox and the relay**

Tests, with a raw `IConsumer` in the test to read the topic:
- `Enqueued_event_is_published_after_commit`: the message arrives with key, `message-id` = `EventId`, `type` and `traceparent` headers; `published_at` is set.
- `Rolled_back_transaction_publishes_nothing`: enqueue, then dispose the transaction without committing → nothing on the topic within 3 s, and the table is empty.
- `Two_relays_publish_each_message_once`: two hosts on one database, 200 messages → 200 received, 200 distinct ids.
- `Relay_survives_a_kafka_outage_and_catches_up`: `PauseAsync` the Kafka container, enqueue, wait `2 × ProduceTimeout` → `attempts` ≥ 1, `/health/live`'s check (call the `IHealthCheck` directly) is `Healthy` because the loop keeps running; `UnpauseAsync` → the message arrives.
- `Relay_check_is_unhealthy_when_the_loop_stalls`: construct `OutboxRelayHealthCheck` with a `RelayHeartbeat` and a `FakeTimeProvider`; stamp now, `Advance(30 s)` → `Unhealthy`.

Use `new KafkaBuilder("apache/kafka:4.4.0")`. Testcontainers 4.x knows the Apache image; if the container doesn't start, fall back to `confluentinc/cp-kafka:8.0.0` (arm64 exists) and record why. Commit: `feat(messaging): transactional outbox with a relay`.

- [ ] **Step 2: Consumer, inbox and dead letters**

- `Handler_runs_once_for_a_duplicate_message`: produce the same message (same `message-id`) twice → the handler ran once, one inbox row.
- `Handler_writes_commit_with_the_inbox_row`: the handler adds a `Thing`; after the message, both the row and the inbox row exist.
- `Poisoned_message_goes_to_the_dead_letter_topic_after_three_attempts`: a handler that always throws → one message on `things.dlt` with the headers; the next message on the topic is handled.
- `Consumer_finishes_the_message_in_flight_before_stopping`: a handler that takes 2 s; stop the host after 500 ms → the handler completed, and a new consumer with the same group doesn't receive the message again.
- `Topics_are_created_once_and_a_second_start_does_not_fail`.

Commit: `feat(messaging): idempotent consumer with a dead-letter topic`.

- [ ] **Step 3: Telemetry**

- `Publish_and_process_spans_share_the_request_trace`: start an `Activity` in the test, enqueue, commit; the producer span and the consumer span (collected with an in-memory exporter on a `TracerProvider` built in the test with source `NewStore.Messaging`) have that activity's `TraceId`.

Commit: `feat(messaging): trace events across the outbox and the consumer`.

- [ ] **Step 4: ADR 0008, docs and the pull request**

1. ADR 0008: the partition key is the aggregate's id (one edition's events stay in order); JSON with `System.Text.Json`, the type in a header, additive changes only, and a schema registry when a second language or a breaking change appears; a dead-letter topic per topic; draining on shutdown; and why at-least-once plus an idempotent consumer, not Kafka transactions.
2. `AGENTS.md`, Architecture rules: how a service publishes (`IOutbox.Enqueue` then `SaveChangesAsync`) and consumes (`AddHandler`), and that Kafka is never in a readiness check. README layout.
3. Walkthrough: two hard parts. Explain the relay's query and `SKIP LOCKED`, the inbox insert, and the dead-letter path, each with its test; suggest one change (for example: remove `FOR UPDATE SKIP LOCKED` and run the two-relay test). Interview angles: "Why an outbox instead of publishing after `SaveChanges`?", "What does exactly-once mean here?", "What happens to the trace across Kafka?". `Closes #37`.

---

### Task 5: Catalog reads: channels, sample videos, search and the cache

V-4 through the API. Branch `phase-1/catalog-reads`, worktree. Issue #38. Advances V-4 (#2); the page is 1b.

**Files:**
- Create `src/Services/Catalog/Channels/`, `Videos/`, `Caching/RedisHealthCheck.cs`, `Seed/CatalogSeeder.cs`, `Seed/sample-catalog.json` (copied to the output and the publish folder), a second migration `AddChannelsAndVideos`.
- Create `tests/Catalog.Tests/VideoTests.cs`, `ChannelTests.cs`, `CachingTests.cs`, `SeedTests.cs`; a `RedisCatalogAppFactory` class fixture with a `Testcontainers.Redis` container (`new RedisBuilder("redis:8.10.3")`).
- Create `docs/adr/0009-catalog-reads-are-cached.md`.
- Modify: `CatalogDbContext.cs`, `Program.cs`, `NewStore.Catalog.csproj` (Microsoft.Extensions.Caching.Hybrid, Microsoft.Extensions.Caching.StackExchangeRedis), `appsettings.Development.json` (`ConnectionStrings:Redis` = `localhost:6379`). By adding lines only: `Directory.Packages.props`, `compose.yaml` (`catalog` gets `ConnectionStrings__Redis: redis:6379`, `depends_on: redis: service_healthy`; `catalog-migrate` command becomes `["migrate", "seed"]`), `.github/workflows/ci.yml` (one curl in `compose-smoke`), `AGENTS.md` Commands, README layout.

**Interfaces:**

```csharp
public sealed class Channel
{
    public Guid Id { get; init; }
    public Guid CreatorId { get; init; }
    public string Name { get; init; }        // max 80
    public string Slug { get; init; }        // unique, lower-case, [a-z0-9-]
    public string Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class Video
{
    public Guid Id { get; init; }
    public Guid ChannelId { get; init; }
    public string Title { get; init; }       // max 120
    public string Description { get; init; }
    public int DurationSeconds { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public NpgsqlTsVector SearchVector { get; }   // generated column over title and description, 'simple' config, GIN index
}
```

| Endpoint | Query | Replies |
|---|---|---|
| `GET /videos` | `q` (optional), `page` ≥ 1 (default 1), `pageSize` 1..100 (default 20) | 200 `Page<VideoSummary>`; 400 for a page or size out of range. Without `q`: newest `publishedAt` first. With `q`: videos whose `SearchVector` matches `websearch_to_tsquery('simple', q)` or whose channel `Name` ILIKE `%q%`, ordered by rank then newest. |
| `GET /videos/{id}` | | 200 `VideoDetails`; 404. Cached. |
| `GET /channels/{slug}` | `page`, `pageSize` | 200 `ChannelPage`; 404. Cached. |

`VideoSummary { id, title, durationSeconds, publishedAt, channel: { id, name, slug } }`; `VideoDetails` adds `description`; `Page<T> { items, page, pageSize, total }`; `ChannelPage { id, name, slug, description, videos: Page<VideoSummary> }`.

- Caching: `AddHybridCache()` with `AddStackExchangeRedisCache` as L2 (`ConnectionStrings:Redis`; `AbortOnConnectFail = false`, `ConnectTimeout` and `SyncTimeout` 1000 ms). Keys `video:{id}` and `channel:{slug}:{page}:{pageSize}`, expiration 60 s, tag `channel:{channelId}`, so 1b can drop a channel's entries when its data changes. Search is not cached. Readiness check `"redis"` with `failureStatus: HealthStatus.Degraded`: a `PING` through a connection the check owns, 3 s budget; not tagged, so it is in `/health/ready` as Degraded, never Unhealthy.
- Seeder: `CatalogSeeder : IDatabaseSeeder<CatalogDbContext>` reads `sample-catalog.json` (6 channels, 48 videos; titles and descriptions with distinctive words; one channel named `Orbital Kitchen`, one video titled `Sourdough in Zero Gravity`, and the words `orbital` and `sourdough` appear nowhere else), inserts only when `channels` is empty.

- [ ] **Step 1: Model, migration, seed (failing tests, then code)**

- `Seed_inserts_the_sample_catalog_once`: run the seeder twice → 6 channels, 48 videos.
- Migration `AddChannelsAndVideos`; check the generated column and the GIN index are in it.

Commit: `feat(catalog): channels, videos and the sample catalog`.

- [ ] **Step 2: Browse and search (failing tests, then code)**

`VideoTests` on a seeded database:
- `Browse_returns_videos_newest_first_with_paging`: page 1 of 20 has 20 items, newest first, `total` 48; page 3 has 8.
- `PageSize_above_100_returns_400`; `Page_zero_returns_400`.
- `Search_by_title_finds_the_video`: `q=sourdough` → includes `Sourdough in Zero Gravity`.
- `Search_by_channel_name_finds_its_videos`: `q=orbital` → every item's channel is `Orbital Kitchen`.
- `Search_with_no_match_returns_an_empty_page`: `q=xyzzy` → 0 items, `total` 0.
- `Video_details_include_the_channel`; `Unknown_video_returns_404_problem`.

`ChannelTests`: `Channel_page_lists_its_videos_newest_first`; `Unknown_channel_returns_404_problem`.

Commit: `feat(catalog): browse, search and channel pages`.

- [ ] **Step 3: Cache and Redis (failing tests, then code)**

`CachingTests(RedisCatalogAppFactory)`:
- `Second_read_of_a_video_is_served_from_the_cache`: request `GET /videos/{id}` twice; find the two server spans by `url.path`, take the later by `StartTimeUtc`; assert no `Npgsql` span has `ParentSpanId` equal to its `SpanId`, while the earlier one has at least one.
- `Reads_still_work_when_redis_is_down`: first find out how HybridCache behaves when L2 throws (write it in the report). Then `StopAsync` the Redis container; `GET /videos/{id}` for an id not yet cached is 200 within 2 s; `GET /health/ready` is 200 with body `Degraded`. If HybridCache propagates the L2 exception, add a `TolerantDistributedCache` decorator around the Redis `IDistributedCache` that catches, logs once per minute and returns null.
- `Ready_is_healthy_with_redis_up`.

Commit: `feat(catalog): HybridCache on Redis, and Redis as a degraded dependency`.

- [ ] **Step 4: ADR 0009, Compose, CI, docs, pull request**

1. ADR 0009: HybridCache (L1 + L2, stampede protection, tags) over a bare `IDistributedCache` or `IMemoryCache`; 60 s staleness is fine for V-4 and E-3 says counts may lag; Redis is Degraded, not Unhealthy, with the reason.
2. `compose.yaml` and `.github/workflows/ci.yml` lines from Files; in `compose-smoke`, `curl --fail 'http://localhost:8101/videos?q=sourdough' | grep -q '"total":1'`. You can't run the stack on the Mac during the parallel run (Task 3 owns it); CI checks it.
3. `AGENTS.md` Commands: the seed command. README layout.
4. Walkthrough: file by file; interview angles: "Where would you put a cache, and what goes wrong when it's a dependency?", "How does full-text search differ from `LIKE`?", "What is a cache stampede and how does HybridCache prevent it?". `Closes #38`.

---

## Phase 1a is done when

1. **In CI:** on `main`, `build-test` and `compose-smoke` are green, and `compose-smoke` includes the k6 run: 1,000 buyers and 10 copies sell exactly 10, numbers 1 to 10 are each used once, expired reservations go back on sale, and reserve's p99 is under 2 s there (under 1 s on the Mac).
2. **ADR 0007** has the measured table for both strategies, with 0 connection timeouts.
3. **On a clean clone:** `docker compose up -d --wait` starts everything, including both migration jobs; `curl 'http://localhost:8101/videos?q=sourdough'` returns one video, and `curl http://localhost:8102/health/ready` prints `Healthy`.
4. **Owner:** sprint 2 ends with the five-question mock interview on Phase 1a topics and the Review and Retro sections of `docs/sprints/sprint-02.md`.
