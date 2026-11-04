# 0004. Build container images with Dockerfiles, not SDK container publishing

## Status

Accepted

## Date

2026-11-05

## Context

Every service needs a container image, for Compose now and Kubernetes in Phase 5. .NET can build one in two ways: a Dockerfile, or `dotnet publish /t:PublishContainer`, which makes an image from the project file with no Dockerfile.

## Decision

Each service has a multi-stage Dockerfile next to its project, and Compose builds it. We try SDK container publishing in Phase 6, when CI builds and pushes images.

## Alternatives considered

- **SDK container publishing.** No Dockerfile to maintain, no Docker daemon needed in CI (it can push to a registry or write a tarball), and it sets the non-root user for you, where a Dockerfile needs `USER $APP_UID`. It does not make a smaller image by default: for a framework-dependent ASP.NET Core app it infers the same `aspnet` base the Dockerfile uses, and a smaller one needs `ContainerFamily` (for example `alpine`) or a chiseled base. The main trade-off is that it has no `RUN`. To install a package or run any build step in the image, we would have to build a custom base image and point `ContainerBaseImage` at it. It also hides the layers, so we would learn a .NET-only tool.
- **A Dockerfile.** More lines to maintain, and each service repeats them. But it shows every layer, the layer cache and the user, and the skill carries over to every other stack.

## Consequences

- We control and can explain every layer: the restore layer is cached until a `.csproj` changes, and the runtime image holds only the published output.
- Each new service copies a Dockerfile, so a fix such as a base-image change is repeated per service. Dependabot's `docker` entry watches each Dockerfile's directory.
- The `aspnet` tag must move with the SDK tag and the package versions, or tests and containers run different runtimes. AGENTS.md says to upgrade them together.
- Dependabot's `docker` entry proposes `sdk` and `aspnet` bumps as their own pull requests, independent of `global.json`. Merge such a pull request only together with the matching `global.json` SDK and the NuGet package versions.
- If this turns out wrong, switching has a moderate cost. The project needs a few `Container*` properties, and the Dockerfile goes. But Compose can't run `dotnet publish`, so:
  - `compose.yaml`'s `build:` block goes, and a publish step must run before Compose, locally and in CI;
  - the smoke job's `up --build` changes with it;
  - the `docker compose build catalog` line in AGENTS.md changes;
  - the bash `/dev/tcp` health check breaks if the base becomes a chiseled image, which has no shell.
