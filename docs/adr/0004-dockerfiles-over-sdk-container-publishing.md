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

- **SDK container publishing.** No Dockerfile to maintain, a small non-root image by default, and no Docker daemon needed in CI. But it hides the layers and the build steps, so we would learn a .NET-only tool, and anything unusual (extra packages, a custom health-check tool) needs the tool's own settings.
- **A Dockerfile.** More lines to maintain, and each service repeats them. But it shows every layer, the layer cache and the user, and the skill carries over to every other stack.

## Consequences

- We control and can explain every layer: the restore layer is cached until a `.csproj` changes, and the runtime image holds only the published output.
- Each new service copies a Dockerfile, so a fix such as a base-image change is repeated per service. Dependabot's `docker` entry watches each Dockerfile's directory.
- The `aspnet` tag must move with the SDK tag and the package versions, or tests and containers run different runtimes. AGENTS.md says to upgrade them together.
- If this turns out wrong, switching is cheap: SDK publishing reads the same project, so we delete the Dockerfile and add a few properties.
