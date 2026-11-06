# 0005. Migrations run as a one-shot job before the service starts

## Status

Accepted

## Date

2026-11-06

## Context

Each service owns its database schema and changes it with EF Core migrations. Something has to apply them before the service handles requests, and the choice decides what a bad migration costs: a job that fails, or a service that never comes up.

## Decision

A service's `Program.cs` runs `RunDatabaseCommandsAsync` right after `Build()`. With the argument `migrate` it applies the pending migrations and exits with code 0 and the host never starts; with no arguments the service runs and touches no schema. In Compose, `catalog-migrate` runs the service's own image with `command: ["migrate"]`, and the service waits for it with `depends_on: { condition: service_completed_successfully }`. A `seed` argument works the same way for initial data.

## Alternatives considered

- **Migrate in the service's startup.** It is the least to set up. EF Core 9 and later take a database-wide lock before applying migrations, so replicas do not corrupt each other ([Applying Migrations, "Migration locking"](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying)). It still loses: the same page lists the costs of runtime migration, namely that the application needs schema-changing permissions, that the SQL is applied without review, and that rollback is harder than with the other strategies; it prefers a separate migration step when least-privilege credentials, coordinated rollout or high availability matter. And a failed migration takes the service down with it, where a failed job leaves the old version serving. The page also says not to make every replica run migrations from its entrypoint.
- **An EF migration bundle** (`dotnet ef migrations bundle`). The page recommends it for automated deployment: an executable that needs no SDK, with the same locking. We use the service's image instead: it already contains the migrations, the configuration and the DI registrations, so there is no second artifact to build and ship. If we later want a bundle, the `migrate` command is the part that would change.
- **A separate migrations project.** It could hold the schema apart from the service and run with other credentials. It is another project to keep in sync with the service's model, and Phase 1 has no need for different credentials.

## Consequences

- A failed migration shows up as a failed job (non-zero exit code), and Compose does not start the service. The old schema keeps serving in an environment that already runs a service.
- `catalog-migrate` uses the same role as the service, because locally each service has one role (ADR 0002). The page advises a separate deployment identity with schema permissions; that comes with the deployed environment, not Compose.
- A new service copies the `Program.cs` shape and adds a `<service>-migrate` entry to `compose.yaml`.
- `dotnet ef migrations add` still runs on the developer's Mac: `ASPNETCORE_ENVIRONMENT=Development dotnet ef migrations add <Name> --project src/Services/<Service>`.
