# 0006. Problem details for every error response, OpenAPI in every environment

## Status

Accepted

## Date

2026-11-06

## Context

Every service has errors a client must handle: unknown paths, validation failures, unhandled exceptions. If each service invents its own error body, every client (the gateway, the web app, other services, tests) needs one parser per service. Services also need a machine-readable description of their API, so clients and tests can read it instead of the source.

## Decision

`AddServiceDefaults` calls `AddProblemDetails()` and `AddOpenApi()`. `MapDefaultEndpoints` adds `UseExceptionHandler()` and `UseStatusCodePages()` first, so an unhandled exception and an empty 4xx/5xx response both become `application/problem+json` ([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)). It also maps the OpenAPI document at `/openapi/v1.json` in every environment. Services call `MapDefaultEndpoints` before any other middleware, so the exception handler wraps the rest of the pipeline.

The OpenAPI document is mapped in every environment because the services are not exposed directly: the gateway (a later phase) decides which routes reach the outside, and it will not route `/openapi/*`. Inside the network the document costs nothing, and tests and the owner's tools can read it in the Compose stack, where the environment is not Development.

## Alternatives considered

- **A hand-written error envelope** (for example `{ "code": "...", "message": "..." }` shared in a library). It gives full control of the shape. The cost is that we own the format, its documentation and its serialization, and no client library or tool knows it. Problem details is a standard that ASP.NET Core supports directly: the exception handler and the status code pages middleware write it once `AddProblemDetails` is registered ([Handle errors in ASP.NET Core APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0)).
- **OpenAPI only in Development.** It is the template's default. It fails here because the Compose stack and CI run in a non-Development environment, so nothing could read the document where the services actually run together.

## Consequences

- Every service answers errors in one shape, with no per-service code.
- The middleware writes problem details only for responses that have no body yet ([same page](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0)). The health endpoints keep their plain `Healthy`/`Unhealthy` body, because the health check writer sets the body before the status code pages run. The Catalog health tests verify this.
- A request whose `Accept` header names only types the default writer does not support (for example `text/html`) does not get problem details ([same page](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0)).
