# AGENTS.md

Guidance for AI coding agents working on newstore.

## What this project is

newstore is the owner's practice project for senior ASP.NET Core backend skills. `README.md` is the design: the requirements as user stories, the architecture, the stack and the phases. Read it before starting work. Work through the phases in order, and finish each phase's "done when" test before starting the next. Each phase has an implementation plan in `docs/plans/`. The rules below apply to every plan.

## Who writes what

The agent writes the code and presents it to the owner in small pieces:

- Deliver each piece as a pull request that covers one user story or one part of a phase.
- The pull request description is the walkthrough: what the code does, how to run it, and the reasoning described in "Teach while building".
- The owner reviews and merges every pull request.

The hard parts get a deeper walkthrough:

- reservations and copy numbers
- the outbox
- the checkout saga
- idempotent consumers
- the courier's live updates

For each of these, explain the code step by step and show the test that proves it works. Then suggest one small change the owner can make to check their understanding.

## Teach while building

The owner is learning, so every change comes with its reasoning:

- why this approach was chosen
- the main alternative, and the trade-off between them
- the question an interviewer would ask about it

Keep it to a few sentences per decision. Every statement about how a framework or tool behaves is checked against its documentation or a command you ran, with the source linked. Unchecked, it stays out of the walkthrough.

## Git, pull requests and the board

- Each task gets its own branch, `phase-<n>/<slug>`, for example `phase-0/catalog-health`, and becomes one pull request.
- Start each branch from the latest `main`, after the owner has merged the previous pull request.
- Commit messages follow Conventional Commits: `feat(catalog): …`, `ci: …`, `docs: …`.
- The pull request body contains `Closes #<issue>`, so merging closes the issue.
- Keep the task's card current on the GitHub Projects board: set it to In progress when work starts and to Review when the pull request opens. Merging closes the issue, and the board's workflow moves the card to Done.
- Each task's pull request also ticks that task's checkboxes in its plan, so the plan shows how far the phase has got. `docs/getting-started.md` explains how a fresh session finds where work stopped.
- A pull request merges only after CI passes, so `main` stays green.

## Architecture rules

- Each service reads and writes only its own database. It learns about other services' data through their APIs and Kafka events.
- Publish every event through the transactional outbox, and make every consumer idempotent.
- Events (`NewStore.Messaging`, ADR 0008):
  - To publish, call `IOutbox.Enqueue(event, topic, key)` in the same unit of work as the business change, then `SaveChangesAsync`. The key is the aggregate's id. Never produce to Kafka directly.
  - To consume, write an `IIntegrationEventHandler<TEvent>` and register it with `AddMessaging<TContext>(m => m.AddHandler<TEvent, THandler>("topic"))`. The handler needs no idempotency code: it runs inside the inbox transaction, once per message id, and its writes commit with the inbox row.
  - The context's model calls `modelBuilder.AddOutbox()` and `modelBuilder.AddInbox()`, and the service's migration creates `outbox_messages` and `inbox_messages`. `AddTopic` creates topics at startup.
  - An event that another service consumes is a record in `NewStore.Contracts` (`src/Shared/Contracts/<Producer>/`), because the consumer picks the class by the CLR full name in the `type` header. The record holds its topic name and partition count as constants, and both ends declare the topic with them.
  - `MessagingCleanupService` deletes published outbox rows one day after publishing and inbox rows after seven days (`Messaging:OutboxRetention`, `Messaging:InboxRetention`). A consumer that replays older messages raises the inbox retention first, or they are handled again.
  - A handler that calls another service lets `HttpRequestException` and timeouts propagate, or throws `TransientFailureException`. The consumer retries those without a limit, except an `HttpRequestException` with a 4xx status other than 408 and 429, which is permanent; any other exception is dead-lettered after `MaxHandlerAttempts`.
  - Kafka is never in a readiness check: a service without Kafka still serves reads. The relay's `outbox-relay` check is a live check only.
- Each service enforces its own permissions with authorization policies, in addition to the gateway's token check.
- Record any choice with a real trade-off as a decision record in `docs/adr/`. Check each claim about an alternative against its documentation before writing it, and link the page.
- When a change affects something the design describes, update `README.md` in the same change.

## Code

- Target .NET 10. A .NET 11 preview SDK is also installed and `dotnet` uses it by default, so `global.json` pins SDK 10.0.403. It also selects the Microsoft.Testing.Platform test runner, which xUnit 4 needs on the .NET 10 SDK. Upgrade the SDK, the `aspnet` image tag and the Microsoft.AspNetCore.* package versions together, so tests, containers and CI run the same runtime: SDK 10.0.403 ships runtime 10.0.14.
- `Directory.Build.props` sets the target framework, nullable reference types, implicit usings and warnings as errors for every project, so project files leave these out.
- Package versions live only in `Directory.Packages.props`. A project's `PackageReference` has no `Version`.
- Migrations are generated code. The root `.editorconfig` marks `**/Migrations/*.cs` as generated, so migration files carry no pragmas and no per-folder `.editorconfig`.
- A service's `Program.cs` takes this shape (see ADR 0005). `migrate` and `seed` are one-shot commands: `RunDatabaseCommandsAsync` returns the exit code, and the host never starts. `migrate` applies pending migrations. `seed` calls the service's `IDatabaseSeeder<TContext>`, which must be safe to run every time. Arguments that start with `-` or `/` are configuration, not commands.

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

- Errors are `application/problem+json` and every service serves `/openapi/v1.json` (ADR 0006). Call `MapDefaultEndpoints` before any other middleware.
- A library's `ActivitySource` and `Meter` are named `NewStore.<Something>`, so `AddServiceDefaults` collects them without a change.
- Each service is one project with feature folders. Split it into layers only when a real need appears, and record why.
- A service's Dockerfile copies the `.csproj` of every project it references, directly or not, before `dotnet restore`. A missing one fails `dotnet publish --no-restore` with NETSDK1004. Check it with `docker compose build <service>` whenever a project reference changes.

## Tests

- Run integration tests against the real PostgreSQL and Kafka a service uses, through Testcontainers. Pass the image tag to the builder, as in `new PostgreSqlBuilder("postgres:18.6")`. The parameterless constructor is obsolete, which fails the build.
- Test projects set `<OutputType>Exe</OutputType>` and `<Using Include="Xunit" />`. `NewStore.TestSupport` is a class library: it references `xunit.v3.extensibility.core`, because the umbrella `xunit.v3` package fails the build of a project that is not an executable.
- Use `NewStore.TestSupport` (`tests/Shared/`) and never copy a factory: `NewStoreAppFactory<Program>` starts a service, `PostgresServer` is one PostgreSQL container per test assembly (register it with `[assembly: AssemblyFixture(typeof(PostgresServer))]`) that hands each test class its own database, `KafkaServer` is one Kafka container per test assembly, registered the same way, and `WaitForSpanAsync` waits for an exported span. Each factory sees only its own spans.
- Every project must be listed in `NewStore.slnx`. CI compares the `.csproj` files on disk with the solution and fails on a difference, because `dotnet test --solution` silently skips an unlisted project.
- Pass `TestContext.Current.CancellationToken` to every call that accepts a token. Otherwise analyzer xUnit1051 fails the build.
- `WebApplicationFactory<Program>` uses the `Program` class that .NET 10 generates. Keep it generated: a hand-written `public partial class Program` trips an analyzer.

## Local stack

- Every container image runs on arm64, because the owner's Mac is Apple Silicon. Pin exact image tags in `compose.yaml`.
- Every Compose service has a memory limit, and the whole stack stays under 12 GB.
- Local-only credentials appear only in `compose.yaml`, `deploy/compose/` and `appsettings.Development.json`: `postgres/postgres`, one `<service>/<service>` role per service, and `admin/admin` for Keycloak. Compose publishes every port on 127.0.0.1 only, so these credentials stay on the Mac.
- Host ports:

  | Port | What |
  |---|---|
  | 5432 | PostgreSQL |
  | 9094 | Kafka for the Mac. Containers use `kafka:9092`. |
  | 6379 | Redis |
  | 8180 | Keycloak |
  | 4317, 4318 | OpenTelemetry Collector (gRPC, HTTP) |
  | 8101 | Catalog in Compose |
  | 5101 | Catalog with `dotnet run` |
  | 8102 | Inventory in Compose |
  | 5102 | Inventory with `dotnet run` |

  Each new service takes the next number: 81NN in Compose and 51NN with `dotnet run`.

## Commands

- Build: `dotnet build NewStore.slnx`
- Test everything: `dotnet test --solution NewStore.slnx` (needs Docker running, for Testcontainers)
- One test: `dotnet test --project tests/Catalog.Tests --filter-method "*<TestName>"`
- Start the local stack: `docker compose up -d --wait`. Stop it: `docker compose down`. Wipe its data: `docker compose down -v`.
- Build the Catalog image: `docker compose build catalog`
- Add a migration: `ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add <Name> --project src/Services/<Service>` (the local tool is pinned in `.config/dotnet-tools.json`; the environment variable makes the design-time host read `appsettings.Development.json`).
- Apply migrations in the stack: `docker compose up -d --wait` runs the one-shot `catalog-migrate` job before `catalog` starts, and `--wait` succeeds although that job exits.
- Run the last-copy load test (1000 buyers, 10 copies; see `tests/load/README.md`): `docker compose -f compose.yaml -f compose.load.yaml up -d --wait`, then `docker compose -f compose.yaml -f compose.load.yaml run --rm k6`. The overlay shortens Inventory's reservation TTL, so use it for this test only.
- Seed a database: `docker compose up -d --wait` also runs `seed` in the `catalog-migrate` job (the command is `migrate seed`). Against a local database: `dotnet run --project src/Services/Catalog -- seed`.
- Run a service from the IDE against the stack: `dotnet run --project src/Services/Catalog` (http://localhost:5101)
