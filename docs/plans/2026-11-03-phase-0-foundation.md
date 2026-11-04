# Phase 0: Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. On 2026-11-04 the owner chose subagent execution: the `code-writer` agent (`~/.claude/agents/code-writer.md`) builds each task, and the main session reviews it before the owner sees the pull request. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A repository where `docker compose up` starts PostgreSQL, Kafka, Keycloak, Redis, an OpenTelemetry Collector and the Catalog service, and where GitHub Actions builds, tests and smoke-tests every pull request.

**Architecture:** One shared library, `NewStore.ServiceDefaults`, gives every service the same health endpoints and OpenTelemetry setup. Catalog is the first service to use it, with a readiness check on its own PostgreSQL database. The whole local stack runs from one `compose.yaml` at the repository root. All telemetry goes to an OpenTelemetry Collector, which for now only prints it. Phase 4 points the collector at Grafana's backends without touching any service.

**Tech Stack:** .NET 10 (SDK 10.0.403), ASP.NET Core minimal hosting, Npgsql 10, OpenTelemetry .NET 1.20, xUnit 4 on Microsoft Testing Platform, Testcontainers 4.16, Docker Compose, GitHub Actions.

**Spec:** [`README.md`](../../README.md): the Phases table (row 0), Constraints, How we work and Decision records. Also [`AGENTS.md`](../../AGENTS.md).

All paths below are relative to the newstore repository root, `example/newstore/`.

## Global Constraints

Every task follows `AGENTS.md`: its sections on git and the board, code, tests and the local stack apply to every step, and its host port table is binding. This section adds only what is specific to Phase 0.

- Package versions, added to `Directory.Packages.props` as tasks need them:
  - Npgsql, Npgsql.DependencyInjection and Npgsql.OpenTelemetry `10.0.3`
  - OpenTelemetry.* `1.20.1`
  - xunit.v3 `4.0.2`
  - Microsoft.AspNetCore.Mvc.Testing `10.0.14`
  - Testcontainers.PostgreSql `4.16.0`
- Image tags, all with arm64 builds:
  - `postgres:18.6`
  - `apache/kafka:4.4.0`
  - `redis:8.10.3`
  - `quay.io/keycloak/keycloak:26.8.1`
  - `otel/opentelemetry-collector-contrib:0.164.0`
  - `mcr.microsoft.com/dotnet/sdk:10.0.403` and `mcr.microsoft.com/dotnet/aspnet:10.0.14`
- The Phase 0 host ports were all free on the owner's Mac on 2026-11-03.

## Review Focus

1. **Missing or empty connection string:** Catalog refuses to start, with a message naming `ConnectionStrings:Catalog`. Test: `Startup_fails_when_connection_string_is_missing` (Task 3).
2. **Hanging database:** the server accepts the TCP connection and never answers. `/health/ready` answers 503 within 5 seconds instead of hanging the probe. Test: `Ready_answers_within_five_seconds_when_database_hangs` (Task 3; Task 5 makes it independent of the network).
3. **Database down:**
   - `/health/live` stays 200, so an orchestrator doesn't restart a process that is fine.
   - `/health/ready` returns exactly `Unhealthy`, with no exception text that would leak internals.
   - Tests: `Live_stays_healthy_when_database_is_down` and `Ready_returns_unhealthy_when_database_refuses_connections` (Task 3).
4. **Unreachable collector:** Catalog keeps serving. A telemetry export failure never breaks a request. Test: `Service_keeps_serving_when_collector_is_unreachable` (Task 5).
5. **Container user:** the Catalog container runs as the non-root user `1654`, not root. Check: `docker compose exec catalog id -u` (Task 7).

## File Structure

```
newstore/
├── global.json                    SDK pin and test runner
├── Directory.Build.props          shared build settings
├── Directory.Packages.props       every package version
├── NewStore.slnx
├── .editorconfig  .gitignore  .gitattributes  .dockerignore
├── compose.yaml                   the local stack
├── .github/workflows/ci.yml       build, format check, tests, Compose smoke test
├── deploy/compose/
│   ├── postgres/init.sql          a database and a role per service
│   └── otel-collector/config.yaml
├── src/Shared/ServiceDefaults/
│   ├── NewStore.ServiceDefaults.csproj
│   ├── ServiceDefaultsExtensions.cs      AddServiceDefaults, MapDefaultEndpoints
│   ├── HealthChecks/HealthCheckExtensions.cs
│   ├── HealthChecks/PostgresHealthCheck.cs
│   └── Telemetry/TelemetryExtensions.cs
├── src/Services/Catalog/
│   ├── NewStore.Catalog.csproj
│   ├── Program.cs
│   ├── appsettings.json  appsettings.Development.json
│   ├── Properties/launchSettings.json
│   └── Dockerfile
├── tests/Catalog.Tests/
│   ├── NewStore.Catalog.Tests.csproj
│   ├── CatalogAppFactory.cs              app with test settings, no database
│   ├── PostgresCatalogAppFactory.cs      the same app with a Testcontainers database
│   ├── HealthEndpointTests.cs
│   ├── HealthWithoutDatabaseTests.cs
│   ├── StartupTests.cs
│   ├── SpanSink.cs                       thread-safe list for the in-memory exporter
│   └── TracingTests.cs
└── docs/
    ├── adr/template.md  0001-…  0002-…  0003-…  0004-…
    ├── plans/                     this plan
    └── sprints/sprint-01.md
```

Each service is one project with feature folders, not a stack of layer projects. Phase 0 has nothing to layer, and splitting later is cheap.

---

### Task 1: Repository skeleton

**Files:**
- Create: `global.json`, `.gitignore`, `.gitattributes`, `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `NewStore.slnx`, `docs/adr/template.md`
- Keep: `README.md`, `AGENTS.md`, `CLAUDE.md`, `docs/getting-started.md`, `docs/plans/2026-11-03-phase-0-foundation.md`

**Interfaces:**
- Produces: the solution file `NewStore.slnx`, the build settings every later project inherits, and an empty `Directory.Packages.props` that later tasks add versions to.

- [x] **Step 1: Create the git repository**

In `example/newstore/`, run `git init -b main`. Then set the commit identity the owner chose on 2026-11-04, for this repository only, because git has no global identity on this Mac:

```bash
git config user.name infinitywalletwar
git config user.email aniknaemm@gmail.com
```

- [x] **Step 2: Pin the SDK and the test runner**

Run `dotnet new globaljson --sdk-version 10.0.401`, then edit it to exactly:

```json
{
  "sdk": { "version": "10.0.401" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

- [x] **Step 3: Add the repository files**

- Run `dotnet new gitignore` and `dotnet new editorconfig`.
- In `.editorconfig`, set `csharp_style_namespace_declarations = file_scoped:warning`.
- Create `.gitattributes` with the single line `* text=auto eol=lf`. Shell scripts and config files mounted into Linux containers must keep LF line endings.

- [x] **Step 4: Write `Directory.Build.props` and `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props` sets `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` and has an empty `<ItemGroup>`.

- [x] **Step 5: Create the solution and the decision record template**

- Run `dotnet new sln -n NewStore`. The .NET 10 SDK creates `NewStore.slnx`.
- Write `docs/adr/template.md` with these headings: Title, Status (Proposed, Accepted or Superseded by …), Date, Context, Decision, Alternatives considered, Consequences.

- [x] **Step 6: Verify**

Run `dotnet --version`. Expected: `10.0.401`.

Run `git status --short`. Expected: only the files listed above, untracked.

- [x] **Step 7: Commit on `main`**

```bash
git add -A
git commit -m "chore: repository skeleton"
```

- [x] **Step 8 (owner's go-ahead required): Create the GitHub repository**

On 2026-11-04 the owner chose a public repository under the `infinitywalletwar` account. Confirm with the owner just before running:

```bash
gh repo create newstore --public --source . --remote origin --push
```

There's no pull request yet, so give this task's walkthrough in chat.

**Walkthrough points:**
- `global.json`, and why the .NET 11 preview SDK would otherwise win
- `Directory.Build.props` and central package management, which keep a single source of truth across projects
- `.slnx` compared with the old `.sln`
- Interview question: "How do you keep package versions consistent across 20 projects?"

---

### Task 2: Backlog, board and first sprint

**Prerequisite:** gh needs the `project` scope. It was added on 2026-11-04. Check with `gh auth status`.

**Files:**
- Create: `docs/sprints/sprint-01.md`

**Interfaces:**
- Produces:
  - Issues for Tasks 3 to 7. Later PRs close them with `Closes #<n>`.
  - The project board.

- [x] **Step 1: Create labels**

Create these labels: `story`, `task`, and `phase-0` through `phase-7`.

- [x] **Step 2: Create one issue per user story**

- Title: the ID and short name from this table, for example `C-1 Reserve a copy`.
- Body: the story and its acceptance criteria, copied word for word from `README.md`.
- Labels: `story` plus the phase label from this table.

| Phase | Stories |
|---|---|
| phase-1 | A-1 Sign up and sign in · V-4 Browse and search videos · E-1 Create an edition · E-2 Lock an edition once a copy is reserved · E-3 See the price and copies left · C-1 Reserve a copy · C-2 Pay for a reservation · C-3 Receive a copy number · C-4 Never sell more copies than exist · L-1 See my copies |
| phase-2 | V-1 Upload a video · V-2 Convert uploads for streaming · V-3 Watch for free · L-2 Download a digital copy |
| phase-3 | D-1 Create a shipment · D-2 Simulate a courier · D-3 Watch the courier live |
| phase-7 | A-2 Block a user · V-5 Remove a video · E-4 See edition sales |

Expected: 20 story issues.

- [x] **Step 3: Create one issue per remaining Phase 0 task**

- Create issues for Tasks 3 to 7 of this plan.
- Title: the task name. Body: a link to the task in this plan, plus its verification.
- Labels: `task` and `phase-0`.

- [x] **Step 4: Create the project and its Status options**

1. Run `gh project create --owner @me --title newstore`.
2. Get the ID of the Status field with `gh project field-list <number> --owner @me --format json`.
3. Replace its options with Backlog, Ready, In progress, Review and Done, using the GraphQL mutation `updateProjectV2Field` with `singleSelectOptions`. If the mutation is refused, ask the owner to edit the options in the project settings instead.
4. Add every issue with `gh project item-add`.
5. Set the five task issues to Ready and the story issues to Backlog.
6. Link the project to the repository with `gh project link`.

- [x] **Step 5 (owner, in the browser): Finish the board**

The API can't do these, so the owner does them in the browser:

- Switch the view to the Board layout, grouped by Status.
- Set a limit of 2 on the In progress column.
- Turn on the built-in workflows "Item closed → Done" and "Pull request merged → Done".

- [x] **Step 6: Write the sprint note**

Write `docs/sprints/sprint-01.md` with these parts:

- The dates: the day this task runs, to 13 days later.
- The goal: "Phase 0: `docker compose up` starts everything and CI is green."
- The issue numbers from Step 3.
- Empty **Review** and **Retro** headings for the owner to fill in at the end of the sprint. The Retro heading has three prompts: What I learned, What was hard, Open questions.

- [x] **Step 7: Open the first pull request**

On branch `phase-0/board`, commit the sprint note with the message `docs: sprint 1 plan`, push it and open a PR.

**Walkthrough points:**
- Scrumban: why a work-in-progress limit of 2 finishes work faster than starting everything
- How `Closes #n` and the board workflows move cards without manual updates
- Interview question: "What's the difference between Scrum and Kanban, and when would you pick each?"

---

### Task 3: Catalog service with health checks

**Files:**
- Create the ServiceDefaults project:
  - `src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj`. Use `Microsoft.NET.Sdk`, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, and reference `Npgsql`.
  - `src/Shared/ServiceDefaults/ServiceDefaultsExtensions.cs`
  - `src/Shared/ServiceDefaults/HealthChecks/HealthCheckExtensions.cs`
  - `src/Shared/ServiceDefaults/HealthChecks/PostgresHealthCheck.cs`
- Create the Catalog service:
  - `src/Services/Catalog/NewStore.Catalog.csproj`. Use `Microsoft.NET.Sdk.Web`. Reference ServiceDefaults and `Npgsql.DependencyInjection`.
  - `src/Services/Catalog/Program.cs`, `appsettings.json`, `appsettings.Development.json`, `Properties/launchSettings.json`
- Create the test project:
  - `tests/Catalog.Tests/NewStore.Catalog.Tests.csproj`. Reference `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql` and the Catalog project.
  - `tests/Catalog.Tests/CatalogAppFactory.cs`, `PostgresCatalogAppFactory.cs`, `HealthEndpointTests.cs`, `HealthWithoutDatabaseTests.cs`, `StartupTests.cs`
- Modify:
  - `Directory.Packages.props`: add the five packages above, at the versions in Global Constraints.
  - `NewStore.slnx`: add the three projects.
  - `AGENTS.md`, "Commands" section.

**Interfaces:**
- Produces, in `NewStore.ServiceDefaults`. Every later service uses these.

  ```csharp
  public static class ServiceDefaultsExtensions
  {
      // Registers the "self" liveness check. Task 5 adds telemetry here.
      public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder);
      // GET /health/live runs only checks tagged "live".
      // GET /health/ready runs every check.
      public static WebApplication MapDefaultEndpoints(this WebApplication app);
  }

  // namespace NewStore.ServiceDefaults.HealthChecks
  public static class HealthCheckExtensions
  {
      public const string LiveTag = "live";
      // Adds PostgresHealthCheck as "postgres", Unhealthy on failure, with a 3-second timeout.
      public static IHealthChecksBuilder AddPostgres(this IHealthChecksBuilder builder);
  }

  // Runs SELECT 1 through the NpgsqlDataSource from DI, passing the check's CancellationToken.
  internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck;
  ```

- Produces, in the tests. Tasks 5 and 7 build on these.

  ```csharp
  // Applies every entry with IWebHostBuilder.UseSetting in ConfigureWebHost.
  public class CatalogAppFactory : WebApplicationFactory<Program>
  {
      public Dictionary<string, string?> Settings { get; }
  }

  // Starts a PostgreSqlContainer built with new PostgreSqlBuilder("postgres:18.6").
  // Sets Settings["ConnectionStrings:Catalog"] in InitializeAsync.
  // DisposeAsync disposes the base class first, then the container.
  public sealed class PostgresCatalogAppFactory : CatalogAppFactory, IAsyncLifetime;
  ```

  `UseSetting` overrides `appsettings.Development.json`, even with an empty value. This was verified on 2026-11-03.

- Health responses use the default plain-text writer:
  - `Healthy` → 200
  - `Degraded` → 200
  - `Unhealthy` → 503

- [x] **Step 1: Create the projects**

1. Create the projects with `dotnet new web` and `dotnet new classlib`. Write the test `.csproj` by hand.
2. Remove `TargetFramework`, `Nullable` and `ImplicitUsings` from each `.csproj`, because `Directory.Build.props` sets them.
3. Delete the template's `Class1.cs` and its `MapGet("/")`.
4. Set the `http` launch profile to `http://localhost:5101`.
5. In `appsettings.Development.json`, set `ConnectionStrings:Catalog` to `Host=localhost;Port=5432;Database=catalog;Username=catalog;Password=catalog`. This lets the service run from the IDE against the Compose database once Task 6 adds it.
6. Add the projects to the solution with `dotnet sln NewStore.slnx add …`.

- [x] **Step 2: Write the failing tests**

`HealthEndpointTests(PostgresCatalogAppFactory factory) : IClassFixture<PostgresCatalogAppFactory>`

```csharp
[Fact] Live_returns_healthy
    // GET /health/live → 200, body "Healthy"
[Fact] Ready_returns_healthy_when_database_is_reachable
    // GET /health/ready → 200, body "Healthy"
```

`HealthWithoutDatabaseTests`: each test makes its own `await using var factory = new CatalogAppFactory { Settings = { ["ConnectionStrings:Catalog"] = … } }`.

```csharp
const string RefusedDatabase = "Host=127.0.0.1;Port=1;Database=catalog;Username=catalog;Password=catalog;Timeout=2";
const string HangingDatabase = "Host=10.255.255.1;Port=5432;Database=catalog;Username=catalog;Password=catalog;Timeout=30";

[Fact] Ready_returns_unhealthy_when_database_refuses_connections
    // RefusedDatabase: GET /health/ready → 503, body exactly "Unhealthy"
[Fact] Live_stays_healthy_when_database_is_down
    // RefusedDatabase: GET /health/live → 200, body "Healthy"
[Fact] Ready_answers_within_five_seconds_when_database_hangs
    // HangingDatabase: GET /health/ready → 503, Stopwatch elapsed < 5 s
```

`StartupTests`

```csharp
[Fact] Startup_fails_when_connection_string_is_missing
    // Settings["ConnectionStrings:Catalog"] = ""
    // var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    // Assert.Contains("ConnectionStrings:Catalog", ex.Message);
```

- [x] **Step 3: Run the tests and watch them fail**

Run: `dotnet test --solution NewStore.slnx`

Expected: FAIL. The health endpoints return 404, and startup doesn't throw.

- [x] **Step 4: Implement**

- Implement `ServiceDefaultsExtensions`, `HealthCheckExtensions` and `PostgresHealthCheck` as declared in Interfaces.
- In `Program.cs`:
  1. Call `builder.AddServiceDefaults()`.
  2. Read `GetConnectionString("Catalog")`. If it is null or whitespace, throw `InvalidOperationException("Connection string 'ConnectionStrings:Catalog' is missing.")`.
  3. Call `builder.Services.AddNpgsqlDataSource(connectionString)`, then `builder.Services.AddHealthChecks().AddPostgres()`.
  4. After `Build()`, call `app.MapDefaultEndpoints()`.

- [x] **Step 5: Run the tests and watch them pass**

Run: `dotnet test --solution NewStore.slnx`

Expected: 6 tests pass. The hanging-database test takes about 3 seconds, because the health check's timeout stops it.

To run one test, use for example `dotnet test --project tests/Catalog.Tests --filter-method "*Live_returns_healthy"`.

- [x] **Step 6: Record the commands**

Replace the placeholder line in the "Commands" section of `AGENTS.md` with:

```markdown
- Build: `dotnet build NewStore.slnx`
- Test everything: `dotnet test --solution NewStore.slnx` (needs Docker running, for Testcontainers)
- One test: `dotnet test --project tests/Catalog.Tests --filter-method "*<TestName>"`
```

- [x] **Step 7: Commit, push and open a PR**

Commit with the message `feat(catalog): service with liveness and readiness checks`.

**Walkthrough points:**
- Liveness versus readiness, and why liveness never checks the database: restarting the app doesn't fix a database outage, and restarting every replica at once turns an outage into a cascade.
- Health-check timeouts.
- Why health responses carry no details.
- Failing fast on missing configuration.
- `NpgsqlDataSource` as a pooled singleton.
- Testcontainers compared with an in-memory database.
- Interview question: "What goes wrong if your liveness probe checks the database?"

---

### Task 4: CI: build, format check and tests

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/dependabot.yml`
- Modify: `Directory.Build.props`, which gets the NuGet audit policy
- Modify: `.gitignore`, which gets the infrastructure-secret patterns

**Interfaces:**
- Produces:
  - A check named `build-test` on every pull request and every push to `main`. Task 7 adds a second job to this workflow.
  - The commit SHAs `actions/checkout` and `actions/setup-dotnet` are pinned to. Task 7's job reuses the `actions/checkout` pin.

- [x] **Step 1: Write the workflow**

```yaml
name: ci
on:
  push:
    branches: [main]
  pull_request:
permissions:
  contents: read
concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: ${{ github.event_name == 'pull_request' }}
jobs:
  build-test:
    runs-on: ubuntu-24.04
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@<commit SHA> # v7.x.y
      - uses: actions/setup-dotnet@<commit SHA> # v6.x.y
        with:
          global-json-file: global.json
      - run: dotnet restore NewStore.slnx
      - run: dotnet format NewStore.slnx --verify-no-changes --no-restore
      - run: dotnet build NewStore.slnx -c Release --no-restore
      - run: dotnet test --solution NewStore.slnx -c Release --no-build
```

Pin each action to the full 40-character commit SHA of its latest release in that major version, with the exact tag in the trailing comment. Find the latest tag with `gh release view --repo actions/checkout --json tagName`, then the SHA with `gh api repos/actions/checkout/git/ref/tags/<tag> --jq '.object'`. If `.object.type` is `tag` (an annotated tag), dereference it: `gh api repos/actions/checkout/git/tags/<that sha> --jq .object.sha`. Do the same for `actions/setup-dotnet`.

- [x] **Step 2: Add Dependabot, the audit policy and the ignore rules**

1. `.github/dependabot.yml`, version 2, with two entries, both `directory: "/"` and a weekly schedule:
   - `github-actions`, so the SHA pins get update PRs that keep the tag comment current
   - `nuget`, with one group covering minor and patch updates (`patterns: ["*"]`, `update-types: [minor, patch]`), so a one-person project gets one PR a week rather than one per package

   Task 7 adds a `docker` entry once a Dockerfile exists.
2. `Directory.Build.props`, in the existing `PropertyGroup`:

   ```xml
   <!-- Low and moderate advisories, and an unreachable vulnerability source, warn; high and critical still fail. -->
   <WarningsNotAsErrors>$(WarningsNotAsErrors);NU1900;NU1901;NU1902</WarningsNotAsErrors>
   ```
3. `.gitignore`: append a block headed `# Infrastructure state and credentials (Phase 6 adds Terraform and EKS)` with `*.tfstate`, `*.tfstate.*`, `*.tfvars`, `!example.tfvars`, `.terraform/`, `kubeconfig*`, `*.pem` and `*.key`. `.terraform.lock.hcl` stays committed, so don't list it.

Run `dotnet build NewStore.slnx`. Expected: 0 warnings, 0 errors.

- [x] **Step 3: Run the same steps locally**

Run `dotnet format NewStore.slnx --verify-no-changes`. Expected: exit code 0. If it reports changes, run `dotnet format NewStore.slnx` and commit the result.

- [x] **Step 4: Commit, push, open a PR and watch it**

1. Commit with the message `ci: build, format check and tests`. Dependabot, the audit policy and the ignore rules go in a second commit, `build: Dependabot, NuGet audit policy and infrastructure ignores`.
2. Push and open the PR.
3. Run `gh pr checks --watch`. Expected: `build-test` passes. The Testcontainers tests use the runner's Docker.

- [x] **Step 5: Require the check**

The repository is public, so GitHub's Free plan allows branch protection.

```bash
gh api -X PUT repos/{owner}/newstore/branches/main/protection --input - <<'EOF'
{"required_status_checks":{"strict":true,"contexts":["build-test"]},"enforce_admins":false,"required_pull_request_reviews":null,"restrictions":null}
EOF
```

Required reviews stay off. The owner's account opens these PRs, and GitHub doesn't let anyone approve their own PR. Task 7 adds `compose-smoke` to the required checks once GitHub has seen that check name.

**Walkthrough points:**
- A main branch that is always green.
- Least-privilege `GITHUB_TOKEN` permissions.
- Cancelling outdated runs on pull requests, but never on `main`, so every merge commit keeps its own result.
- Why the format check runs before the build.
- Actions pinned by commit SHA rather than by tag. A tag can be moved to other code, as happened to `tj-actions/changed-files` in March 2025 (CVE-2025-30066), when its moved tags leaked CI secrets. A SHA can't move, and Dependabot keeps the pins current.
- Why low and moderate NuGet advisories only warn. With warnings as errors, a newly published advisory would turn `main` red with no commit and block an urgent fix. Dependabot turns it into a pull request instead, and high and critical advisories still fail the build.
- Interview question: "How do you keep the main branch releasable?"

---

### Task 5: OpenTelemetry

**Files:**
- Create: `src/Shared/ServiceDefaults/Telemetry/TelemetryExtensions.cs`
- Create: `tests/Catalog.Tests/TracingTests.cs`, `tests/Catalog.Tests/SpanSink.cs`
- Modify: `ServiceDefaultsExtensions.cs`, which now calls `AddDefaultTelemetry`
- Modify: `CatalogAppFactory.cs`, which gains `ExportedSpans` and runs the app in the `Testing` environment
- Modify: `PostgresCatalogAppFactory.cs`, which becomes an assembly fixture, and `HealthEndpointTests.cs`, which takes it through its constructor
- Modify: `tests/Catalog.Tests/HealthWithoutDatabaseTests.cs`, which gains one test and a hanging database that doesn't depend on the network
- Modify: `tests/Catalog.Tests/StartupTests.cs`, which tests a missing key as well as an empty one
- Modify: `Directory.Packages.props` and both `.csproj` files:
  - ServiceDefaults gets the OpenTelemetry packages:
    - `OpenTelemetry.Extensions.Hosting`
    - `OpenTelemetry.Exporter.OpenTelemetryProtocol`
    - `OpenTelemetry.Instrumentation.AspNetCore`
    - `OpenTelemetry.Instrumentation.Http`
    - `OpenTelemetry.Instrumentation.Runtime`
    - `Npgsql.OpenTelemetry`
  - The tests get `OpenTelemetry.Exporter.InMemory`.
  - ServiceDefaults also gets `Npgsql.DependencyInjection`, for the readiness check's own data source (Step 6).
- Modify: `src/Shared/ServiceDefaults/HealthChecks/HealthCheckExtensions.cs` and `PostgresHealthCheck.cs`, so the readiness check gets a connection pool of its own (Step 6)
- Modify: `src/Services/Catalog/Program.cs`, which passes the connection string to `AddPostgres`
- Modify: `tests/Catalog.Tests/HealthEndpointTests.cs`, which gains one test for a busy request pool
- Create: `docs/adr/0001-readiness-check-has-its-own-connection-pool.md`

**Interfaces:**
- Consumes: `AddServiceDefaults`, `CatalogAppFactory` and `PostgresCatalogAppFactory` from Task 3.
- Produces:

  ```csharp
  // NewStore.ServiceDefaults.Telemetry
  internal static class TelemetryExtensions
  {
      public static IHostApplicationBuilder AddDefaultTelemetry(this IHostApplicationBuilder builder);
  }

  // tests/Catalog.Tests/SpanSink.cs: an ICollection<Activity> that guards a List<Activity> with a
  // System.Threading.Lock and adds Activity[] Snapshot(). The exporter adds spans on request threads
  // while tests read, and List<T> is not safe for that: Add raises the count before it writes the item.
  public sealed class SpanSink : ICollection<Activity>
  {
      public Activity[] Snapshot();
  }

  // CatalogAppFactory, added in ConfigureWebHost through ConfigureTestServices:
  // services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(ExportedSpans))
  public SpanSink ExportedSpans { get; }

  // NewStore.ServiceDefaults.HealthChecks.HealthCheckExtensions, changed in Step 6:
  public const string HealthDataSourceKey = "health";
  public static IHealthChecksBuilder AddPostgres(this IHealthChecksBuilder builder, string connectionString);
  ```

- `CatalogAppFactory.ConfigureWebHost` also calls `builder.UseEnvironment("Testing")`. Tests then don't read `appsettings.Development.json`, which is meant for `dotnet run`: a connection string or an `OTEL_EXPORTER_OTLP_ENDPOINT` added there can't leak into a test. Outside Development, the host stops validating scopes and the service graph on build, so the factory turns both back on (`ValidateScopes` and `ValidateOnBuild`). Check against Microsoft Learn which hook applies them under `WebApplicationFactory` with minimal hosting.
- `PostgresCatalogAppFactory` becomes an xUnit v3 assembly fixture: `[assembly: AssemblyFixture(typeof(PostgresCatalogAppFactory))]` in its file. `HealthEndpointTests` and `TracingTests` take it as a constructor argument and drop `IClassFixture`, so one container and one app serve the whole run. Tests that share it must not assume the span list starts empty.

- `AddDefaultTelemetry` does the following:
  - **Service name:** the `OTEL_SERVICE_NAME` setting if present, otherwise `builder.Environment.ApplicationName`.
  - **Traces:** ASP.NET Core instrumentation, with a `Filter` that drops requests whose path starts with the segment `/health`, plus HttpClient and `AddNpgsql()`.
  - **Metrics:** ASP.NET Core, HttpClient, Runtime and Npgsql, so connection-pool exhaustion shows on a dashboard once readiness no longer reports it (Step 6). Take the meter extension's name from `Npgsql.OpenTelemetry`'s documentation.
  - **Logs:** `builder.Logging.AddOpenTelemetry` with `IncludeFormattedMessage` and `IncludeScopes`.
  - **Export:** `UseOtlpExporter()`, but only when the `OTEL_EXPORTER_OTLP_ENDPOINT` setting isn't empty. Tracing is always registered, so tests can attach the in-memory exporter.

- [x] **Step 1: Write the failing tests**

`TracingTests(PostgresCatalogAppFactory factory)`, with no `IClassFixture`: the factory is the assembly fixture.

The private helper `Task<Activity> WaitForSpanAsync(string path, CancellationToken ct)` polls `factory.ExportedSpans.Snapshot()` every 50 ms, for up to 5 seconds, for a span whose `url.path` tag equals `path`. If none appears, it fails the test.

Server spans end just after the response reaches the client, so tests wait for a span instead of reading the list at once. The exporter adds spans on the request thread while the test reads, so `SpanSink` guards its list with a lock and tests read a `Snapshot()`.

```csharp
[Fact] Request_to_unknown_path_is_traced
    // GET /no-such-page → 404
    // span = await WaitForSpanAsync("/no-such-page", ct)
    // Assert.Equal(ActivityKind.Server, span.Kind); Assert.Equal(404, span.GetTagItem("http.response.status_code"))

[Fact] Health_requests_are_not_traced
    // GET /health/live and GET /health/ready → both 200
    // then GET /after-health and await WaitForSpanAsync("/after-health", ct). That span arriving shows the earlier requests have finished.
    // Assert.DoesNotContain(spans, s => (s.GetTagItem("url.path") as string)?.StartsWith("/health") == true)
    // Assert.DoesNotContain(spans, s => s.Source.Name == "Npgsql")   // the readiness query is not traced either
```

In `HealthWithoutDatabaseTests`:

```csharp
[Fact] Service_keeps_serving_when_collector_is_unreachable
    // Settings: ConnectionStrings:Catalog = RefusedDatabase, OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:1"
    // GET /health/live → 200 "Healthy"; GET /no-such-page → 404
```

Also in `HealthWithoutDatabaseTests`, make the hanging database local. `10.255.255.1` hangs only on a network that silently drops the connection attempt. Offline, or where a router answers "unreachable", the connect fails at once and the test passes without reaching the timeout. Replace it inside `Ready_answers_within_five_seconds_when_database_hangs`:

```csharp
// A listener that is started but never accepts: the kernel completes the TCP handshake, then nothing
// answers, so Npgsql waits for the server's first message until the health pool's own connect Timeout
// (3 seconds, set in AddPostgres) ends it. Timeout=30 stays in this connection string, so only that
// override can end the wait in time.
using var silentServer = new TcpListener(IPAddress.Loopback, 0);
silentServer.Start();
var port = ((IPEndPoint)silentServer.LocalEndpoint).Port;
await using var factory = FactoryFor(
    $"Host=127.0.0.1;Port={port};Database=catalog;Username=catalog;Password=catalog;Timeout=30;SSL Mode=Disable");
```

In `StartupTests`, the `Testing` environment leaves the key absent, so test both shapes:

```csharp
[Theory] [InlineData(null)] [InlineData("")]
Startup_fails_when_connection_string_is_missing_or_empty(string? value)
    // null: no Settings entry, so the key is absent; "": the key is set and empty
    // Assert.Throws<InvalidOperationException>(() => factory.CreateClient()); message contains "ConnectionStrings:Catalog"
```

- [x] **Step 2: Run the tests and watch them fail**

Run: `dotnet test --solution NewStore.slnx`

Expected: the two tracing tests fail, because no spans are exported. If `ExportedSpans` is missing, compilation fails instead.

The changed hanging-database test passes before and after, because it tests code from Task 3. Prove it still tests something: run it alone and check that it takes about 3 seconds. Then remove the `Timeout` override from the health pool's connection string in `AddPostgres` and watch it fail at about 30 seconds, then put the override back. (Added in a fix round: the cancellation token does not reach Npgsql's startup handshake, so `timeout:` alone does not end this wait.)

- [x] **Step 3: Implement `AddDefaultTelemetry` and call it from `AddServiceDefaults`**

- [x] **Step 4: Run the tests and watch them pass**

Run: `dotnet test --solution NewStore.slnx`

Expected: 10 tests pass. The startup theory counts as two.

- [x] **Step 5: Commit**

Commit with the message `feat(observability): OpenTelemetry traces, metrics and logs`.

- [x] **Step 6: Give the readiness check its own connection pool**

The check now borrows a connection from the pool that serves requests. When traffic fills that pool, the check waits for a free connection, its 3-second timeout fires, and readiness reports Unhealthy. Kubernetes then takes a pod that is only busy out of rotation, and its load moves to pods that are just as busy. A readiness probe should fail only when the pod can't do its job at all.

1. Write the failing test in `HealthEndpointTests`. It builds its own app on the fixture's container with a request pool of one connection, and holds that connection:

   ```csharp
   [Fact] Ready_stays_healthy_when_request_pool_is_exhausted
       // csb = new NpgsqlConnectionStringBuilder(factory.Settings["ConnectionStrings:Catalog"]) { MaxPoolSize = 1, Timeout = 30 }
       // await using var busyApp = new CatalogAppFactory { Settings = { ["ConnectionStrings:Catalog"] = csb.ConnectionString } }
       // using var client = busyApp.CreateClient()
       // await using var held = await busyApp.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct)
       // GET /health/ready → 200 "Healthy", in under 5 seconds (Stopwatch)
   ```

2. Run it alone. Expected: FAIL with 503 after about 3 seconds, because the check waits for the held connection.
3. Implement. `AddPostgres(connectionString)` registers a keyed `NpgsqlDataSource` under `HealthDataSourceKey` with `services.AddNpgsqlDataSource(..., serviceKey: HealthDataSourceKey)`. It is built from the same connection string with `MaxPoolSize = 2`, through `NpgsqlConnectionStringBuilder`. The check is registered through a `HealthCheckRegistration` whose factory passes `GetRequiredKeyedService<NpgsqlDataSource>(HealthDataSourceKey)` to `PostgresHealthCheck`, with the same name, failure status and 3-second timeout as before. `Program.cs` calls `AddPostgres(connectionString)`. `Degraded` isn't needed: the request pool's state no longer reaches the check.
4. Run `dotnet test --solution NewStore.slnx`. Expected: 11 tests pass.
5. Write `docs/adr/0001-readiness-check-has-its-own-connection-pool.md` from `docs/adr/template.md`, status Accepted. It compares sharing the request pool with a pool of its own:
   - **A shared pool:** readiness also reports a full pool, and under load that turns into a readiness cascade.
   - **A pool of its own:** readiness answers "is PostgreSQL reachable?". It stays green while requests queue for a connection, so pool exhaustion has to show in the Npgsql metrics and alerts instead.
6. Commit with the message `fix(health): readiness check gets its own connection pool`.

- [x] **Step 7: Push and open a PR**

Expected: CI is green.

**Walkthrough points:**
- The three signals: traces, metrics and logs.
- OTLP, and why services talk only to a collector.
- Why health checks are filtered out, and how the filter also drops the child spans, because of parent-based sampling.
- Why `List<T>` needs a lock when the exporter writes and the test reads.
- Fixture scope (class, collection, assembly) as the knob that keeps integration suites fast, and what sharing one app costs.
- A test environment of its own, and the DI validation it would silently turn off.
- A test that can pass without testing anything: how to prove it fails when the code it guards is removed.
- A readiness probe fails only when the pod can't do its job, never because it is busy. What a pool of its own costs: a full request pool now shows only in metrics.
- Interview question: "Your readiness probe checks the database. What happens to the fleet during a traffic spike?"
- `service.name`.
- W3C `traceparent` propagation, which Phase 1 relies on across services.
- Interview question: "How would you follow one request across five services?"

---

### Task 6: Local infrastructure in Docker Compose

**Files:**
- Create: `compose.yaml`, `deploy/compose/postgres/init.sql`, `deploy/compose/otel-collector/config.yaml`
- Create: `docs/adr/0002-one-postgres-server-with-a-database-and-role-per-service.md`
- Create: `docs/adr/0003-all-telemetry-goes-through-the-collector.md`
- Modify: `AGENTS.md`, "Commands" section

**Interfaces:**
- Produces:
  - The Compose service names `postgres`, `kafka`, `redis`, `keycloak` and `otel-collector`, at the host ports in `AGENTS.md`.
  - For containers, Kafka listens on `kafka:9092`. For the Mac, it listens on `localhost:9094`.
  - The `catalog` database, owned by the role `catalog` with the password `catalog`.

- [x] **Step 1: Write `deploy/compose/postgres/init.sql`**

```sql
CREATE ROLE catalog LOGIN PASSWORD 'catalog';
CREATE DATABASE catalog OWNER catalog;
REVOKE ALL ON DATABASE catalog FROM PUBLIC;
```

Each later service adds the same three lines for itself. PostgreSQL runs init scripts only when the data volume is empty, so after editing this file run `docker compose down -v`.

- [x] **Step 2: Write `deploy/compose/otel-collector/config.yaml`**

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317   # the default is localhost, which other containers can't reach
      http:
        endpoint: 0.0.0.0:4318
exporters:
  debug/traces:
    verbosity: detailed
  debug:
    verbosity: basic
service:
  pipelines:
    traces:  { receivers: [otlp], exporters: [debug/traces] }
    metrics: { receivers: [otlp], exporters: [debug] }
    logs:    { receivers: [otlp], exporters: [debug] }
```

- [x] **Step 3: Write `compose.yaml`**

```yaml
name: newstore

services:
  postgres:
    image: postgres:18.6
    environment:
      POSTGRES_PASSWORD: postgres
    ports: ["127.0.0.1:5432:5432"]
    volumes:
      - postgres-data:/var/lib/postgresql   # PostgreSQL 18 moved its volume here from /var/lib/postgresql/data
      - ./deploy/compose/postgres/init.sql:/docker-entrypoint-initdb.d/init.sql:ro
    healthcheck:
      # Over TCP: while init.sql runs, the entrypoint's temporary server listens only on the Unix socket.
      test: ["CMD-SHELL", "pg_isready -h 127.0.0.1 -U postgres -d postgres"]
      interval: 5s
      timeout: 3s
      retries: 10
    deploy: { resources: { limits: { memory: 512m } } }

  kafka:
    image: apache/kafka:4.4.0
    ports: ["127.0.0.1:9094:9094"]
    environment:
      KAFKA_NODE_ID: 1
      KAFKA_PROCESS_ROLES: broker,controller
      KAFKA_LISTENERS: INTERNAL://:9092,CONTROLLER://:9093,EXTERNAL://:9094
      KAFKA_ADVERTISED_LISTENERS: INTERNAL://kafka:9092,EXTERNAL://localhost:9094
      KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: INTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT,EXTERNAL:PLAINTEXT
      KAFKA_INTER_BROKER_LISTENER_NAME: INTERNAL
      KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
      KAFKA_CONTROLLER_QUORUM_VOTERS: 1@kafka:9093
      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
      KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR: 1
      KAFKA_TRANSACTION_STATE_LOG_MIN_ISR: 1
      KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: 0
      KAFKA_AUTO_CREATE_TOPICS_ENABLE: "false"
      KAFKA_HEAP_OPTS: "-Xms512m -Xmx512m"
    healthcheck:
      test: ["CMD-SHELL", "/opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server localhost:9092 > /dev/null 2>&1"]
      interval: 10s
      timeout: 10s
      retries: 10
      start_period: 15s
    deploy: { resources: { limits: { memory: 1g } } }

  redis:
    image: redis:8.10.3
    ports: ["127.0.0.1:6379:6379"]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 10
    deploy: { resources: { limits: { memory: 256m } } }

  keycloak:
    image: quay.io/keycloak/keycloak:26.8.1
    command: start-dev
    environment:
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_BOOTSTRAP_ADMIN_PASSWORD: admin
      KC_HEALTH_ENABLED: "true"
    ports: ["127.0.0.1:8180:8080"]
    healthcheck:
      # The image has no curl, so the check speaks HTTP to the management port through bash.
      test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/9000 && printf 'GET /health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -q 'HTTP/1.1 200' <&3"]
      interval: 10s
      timeout: 5s
      retries: 15
      start_period: 30s
    deploy: { resources: { limits: { memory: 1g } } }

  otel-collector:
    image: otel/opentelemetry-collector-contrib:0.164.0
    volumes:
      - ./deploy/compose/otel-collector/config.yaml:/etc/otelcol-contrib/config.yaml:ro
    ports: ["127.0.0.1:4317:4317", "127.0.0.1:4318:4318"]
    deploy: { resources: { limits: { memory: 256m } } }

volumes:
  postgres-data:
```

Some of these choices need a word of explanation:

- **Kafka has no volume.** Its data survives `stop` and `start` but not `down`. That's fine for local work, and Phase 1 creates topics from code.
- **The collector image has no shell,** so it has no health check. Compose treats it as ready once it's running.
- **Keycloak keeps its data inside the container** in dev mode. Phase 1 imports the realm from a file, so it's reproducible.

- [x] **Step 4: Start the stack and check each part**

Run `docker compose up -d --wait`. Expected: exit code 0, with every container that has a health check marked healthy. Then:

| Command | Expected |
|---|---|
| `docker compose exec postgres psql -U catalog -d catalog -tAc "select current_user"` | `catalog` |
| `docker compose exec postgres psql -U postgres -tAc "select has_database_privilege('public', 'catalog', 'CONNECT')"` | `f` |
| `docker compose exec kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list` | exit code 0, empty output |
| `nc -z localhost 9094` | succeeds |
| `docker compose exec redis redis-cli ping` | `PONG` |
| `curl -s -o /dev/null -w '%{http_code}' http://localhost:8180/realms/master` | `200` |
| `docker compose logs otel-collector \| grep -c "Everything is ready"` | `1` |
| `docker stats --no-stream` | no container above its limit |

Then run `dotnet run --project src/Services/Catalog`, and in a second terminal run `curl -s http://localhost:5101/health/ready`. Expected: `Healthy`. This checks the IDE workflow against the Compose database.

- [x] **Step 5: Write the decision records and the commands**

Write `0002` and `0003` from `docs/adr/template.md`:

- **0002** compares one PostgreSQL container per service with one server. On one server, the role per service plus `REVOKE … FROM PUBLIC` keeps the rule "each service reads only its own database" enforced by the database itself. In AWS, RDS can do the same, or split later.
- **0003** compares services exporting straight to the backends, or to the Aspire dashboard, with exporting through the collector. Through the collector, services never change when the backends do (Phase 4).

Add these lines to the "Commands" section of `AGENTS.md`:

```markdown
- Start the local stack: `docker compose up -d --wait`. Stop it: `docker compose down`. Wipe its data: `docker compose down -v`.
- Run a service from the IDE against the stack: `dotnet run --project src/Services/Catalog` (http://localhost:5101)
```

- [x] **Step 6: Commit, push and open a PR**

Commit with the message `feat(infra): local stack with PostgreSQL, Kafka, Keycloak, Redis and the collector`.

**Walkthrough points:**
- Health checks plus `depends_on` conditions, and why `pg_isready` needs `-h 127.0.0.1`: without it, Compose can call PostgreSQL healthy while `init.sql` is still running.
- KRaft versus ZooKeeper. Kafka 4 removed ZooKeeper entirely.
- Advertised listeners, the classic "works inside the container, fails from my laptop" bug.
- Why topics aren't created automatically.
- How the database enforces database-per-service.
- The PostgreSQL 18 volume path.
- Init scripts run only once.
- Interview question: "A client on your laptop can't reach Kafka in Docker, but `kafka-topics.sh` inside the container works. Why?"

---

### Task 7: Catalog in a container, and the Compose smoke test

**Files:**
- Create: `src/Services/Catalog/Dockerfile`, `.dockerignore`
- Create: `docs/adr/0004-dockerfiles-over-sdk-container-publishing.md`
- Modify: `compose.yaml`, which gets the `catalog` service
- Modify: `.github/workflows/ci.yml`, which gets the `compose-smoke` job
- Modify: `.github/dependabot.yml`, which gets a weekly `docker` entry for `src/Services/Catalog`, the Dockerfile's directory
- Modify: `README.md`, Status line: Phase 0 done once this PR merges

**Interfaces:**
- Consumes:
  - The Compose services from Task 6.
  - `MapDefaultEndpoints` and the `OTEL_*` settings from Tasks 3 and 5.
- Produces:
  - The image `newstore/catalog:local`.
  - Catalog at `http://localhost:8101`.
  - A second CI check, `compose-smoke`.

- [ ] **Step 1: Write the Dockerfile**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj src/Shared/ServiceDefaults/
COPY src/Services/Catalog/NewStore.Catalog.csproj src/Services/Catalog/
RUN dotnet restore src/Services/Catalog/NewStore.Catalog.csproj
COPY src/ src/
RUN dotnet publish src/Services/Catalog/NewStore.Catalog.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
USER $APP_UID
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NewStore.Catalog.dll"]
```

The `.csproj` files are copied before the source so the restore layer is cached until a dependency changes. `.editorconfig` is copied because warnings are errors, so the build must see the same rules as on the Mac.

`.dockerignore` lists `**/bin/`, `**/obj/`, `.git/`, `.github/`, `docs/`, `tests/`, `web/` and `deploy/`.

- [ ] **Step 2: Add the `catalog` service to `compose.yaml`**

```yaml
  catalog:
    build:
      context: .
      dockerfile: src/Services/Catalog/Dockerfile
    image: newstore/catalog:local
    environment:
      ConnectionStrings__Catalog: Host=postgres;Database=catalog;Username=catalog;Password=catalog
      OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317
      OTEL_SERVICE_NAME: catalog
    ports: ["127.0.0.1:8101:8080"]
    depends_on:
      postgres: { condition: service_healthy }
      otel-collector: { condition: service_started }
    healthcheck:
      # The runtime image has bash but no curl, so the probe speaks HTTP through /dev/tcp, as Keycloak's does.
      test: ["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && grep -q 'HTTP/1.1 200' <&3"]
      interval: 5s
      timeout: 5s
      retries: 10
      start_period: 10s
    deploy: { resources: { limits: { memory: 512m } } }
```

Without a health check, `docker compose up --wait` would return as soon as the container runs, before Kestrel listens, and the first `curl` would get no answer. With it, `--wait` returns only once `/health/ready` is 200. Kubernetes replaces the probe with an HTTP probe in Phase 5.

- [ ] **Step 3: Check it on the Mac**

| Command | Expected |
|---|---|
| `docker compose up -d --build --wait` | exit code 0 |
| `curl -s -w ' %{http_code}\n' http://localhost:8101/health/ready` | `Healthy 200` |
| `docker compose ps catalog --format '{{.Health}}'` | `healthy` |
| `docker compose exec catalog id -u` | `1654` |
| `curl -s -o /dev/null http://localhost:8101/no-such-page`, wait 5 s, then `docker compose logs otel-collector \| grep -c "service.name: Str(catalog)"` | at least `1` |
| `docker compose stop postgres`, then `curl -s -w ' %{http_code}\n' http://localhost:8101/health/ready`, then `docker compose start postgres` | `Unhealthy 503`, then `Healthy 200` after the restart |

- [ ] **Step 4: Add the smoke-test job to `ci.yml`**

```yaml
  compose-smoke:
    runs-on: ubuntu-24.04
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@<the SHA from Task 4> # the same tag comment
      - name: Start the stack
        run: docker compose up --build --detach --wait --wait-timeout 240
      - name: Catalog is ready
        # --wait already waited for Catalog's health check; the retry is a second line of defence.
        run: curl --fail --silent --show-error --retry 30 --retry-delay 2 --retry-all-errors http://localhost:8101/health/ready
      - name: Show logs
        if: failure()
        run: docker compose logs
      - name: Stop the stack
        if: always()
        run: docker compose down --volumes
```

- [ ] **Step 5: Write ADR 0004 and update the status**

- **ADR 0004** compares a Dockerfile with `dotnet publish /t:PublishContainer`:
  - SDK container publishing needs no Dockerfile and makes small, non-root images.
  - A Dockerfile is the skill that carries over to every other stack, and it shows every layer.
  - The decision is to use a Dockerfile now and try SDK publishing in Phase 6.
- Set the `README.md` Status line to: Phase 0 done, Phase 1 next.
- Add `- Build the Catalog image: docker compose build catalog` to the "Commands" section of `AGENTS.md`.

- [ ] **Step 6: Commit, push, open a PR and watch it**

1. Commit with the message `feat(catalog): container image and Compose smoke test in CI`.
2. Push and open the PR.
3. Run `gh pr checks --watch`. Expected: `build-test` and `compose-smoke` both pass.

- [ ] **Step 7: Require the new check**

GitHub lists only check names it has seen, so this runs after the first green `compose-smoke` run. Until then the smoke test is advisory, and a red run wouldn't block a merge.

```bash
gh api -X PATCH repos/{owner}/newstore/branches/main/protection/required_status_checks --input - <<'EOF'
{"strict":true,"contexts":["build-test","compose-smoke"]}
EOF
```

Run `gh api repos/{owner}/newstore/branches/main/protection/required_status_checks --jq .contexts`. Expected: both names.

**Walkthrough points:**
- Multi-stage builds and layer caching.
- Non-root containers.
- Why the image has no curl, and how the health check works without it.
- How `ConnectionStrings__Catalog` becomes `GetConnectionString("Catalog")`.
- `depends_on` with health conditions.
- Interview question: "Why does the order of `COPY` lines in a Dockerfile matter?"

---

## Phase 0 is done when

1. **On a clean clone:** `docker compose up -d --wait` starts everything, and `curl http://localhost:8101/health/ready` prints `Healthy`.
2. **In CI:** on `main`, the latest run shows `build-test` and `compose-smoke` green.
3. **Owner:** sprint 1 ends with these two steps:
   - The agent gives a five-question mock interview on Phase 0 topics, and the owner answers in their own words.
   - The owner fills in the Review and Retro sections of `docs/sprints/sprint-01.md`.
