# Creator is a resource rule, not a role

## Status

Accepted

## Date

2026-11-13

## Context

A viewer becomes a creator by opening a channel (A-1). From then on the creator may create editions for their own videos and change only their own editions (E-1, E-2). The README says a creator "is not a role": the permission compares the token's `sub` with the resource's `CreatorId`. Two services are involved. Catalog knows who owns a video, and Inventory owns the editions.

## Decision

Creator is a rule about a resource, checked in the service that owns the resource.

- **Editions: a resource-based handler.** `EditionOwnerHandler` (`AuthorizationHandler<EditionOwnerRequirement, Edition>`) succeeds when the token's `sub` equals `edition.CreatorId`. The PATCH, end-sale and copies endpoints require `SignedIn`, load the edition, and call `IAuthorizationService.AuthorizeAsync(user, edition, new EditionOwnerRequirement())`. A refusal is a 403 `not-your-edition`.
- **Creating an edition: ask the owner of the video.** Inventory asks Catalog, `GET /videos/{id}/owner`, and compares the answer with the token's `sub`. Someone else's video is a 403 `not-your-video`; an unknown video is a 400 on `videoId`; an unreachable Catalog is a 503 `dependency-unavailable`. That endpoint accepts only the client `inventory`.
- **`CreatorId` never comes from the request.** `CreateEditionRequest` has no creator field. The edition's `CreatorId` is the token's `sub`, so no caller can create an edition as someone else.
- **Reading an edition stays public.**

The Microsoft documentation gives the reason a handler is called from code and not from an attribute: "Attribute evaluation occurs before data binding and before execution of any method that loads a resource. Declarative authorization with an `[Authorize]` attribute doesn't suffice for resource-based authorization. Instead, the app must invoke a custom authorization method—an approach known as *imperative authorization*." ([Resource-based authorization in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0), introduction.)

## Alternatives considered

- **A `creator` realm role in Keycloak.** A role says "this user may sell editions". It cannot say "this edition is theirs", so every endpoint would still need the ownership comparison, and the role would add a second thing to keep in step with "has a channel". The rule we have needs no state outside the two services' data.
- **A policy that reads the request body.** To use a `creatorId` from the body, a policy would trust a field the caller writes. The creator comes from the token, so there is nothing to check. (A handler that checks a loaded resource, as chosen, is the pattern the quoted page describes.)
- **Copy the video's owner into Inventory (a Kafka snapshot, as `EditionSnapshot` flows the other way).** The check would then be a local read and Inventory would work with Catalog down. It adds a topic, a consumer and a window in which a new video is not yet known. We chose the call because a creator creates an edition rarely, and a slow or failed create while Catalog is down is acceptable (see Consequences for how slow). If creation became frequent or Catalog's availability mattered more, the snapshot is the next step.
- **404 instead of 403 for someone else's edition.** RFC 9110 section 15.5.4 says: "An origin server that wishes to "hide" the current existence of a forbidden target resource MAY instead respond with a status code of 404 (Not Found)." ([RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-403-forbidden)). Hiding only helps if the resource is secret. An edition is public: `GET /editions/{id}` answers anyone, so 404 would hide nothing and would make a wrong id and a wrong owner look the same to an honest creator. We answer 403 with a problem type the client can act on.

## Consequences

- **PATCH, end-sale and copies read the edition first.** Authorizing needs the edition, so each does one extra `SELECT` (`AsNoTracking`) before its write. The write is unchanged: PATCH is still one conditional `UPDATE` and ADR 0010's guarantee holds. The read may be stale by the time the `UPDATE` runs, but `CreatorId` is never updated, so a stale read cannot give a different answer to the ownership question.
- **Creating an edition depends on Catalog, and a slow Catalog is a slow create.** `AddServiceClient` gives Inventory the standard resilience pipeline (ADR 0012). Its documented defaults are a total timeout of 30 s, and retries with "Max retries: `3`", "Backoff: `Exponential`" and "Delay: 2s" ([standard resilience handler defaults](https://learn.microsoft.com/dotnet/core/resilience/http-resilience#standard-resilience-handler-defaults)). So the worst case for `POST /editions` with Catalog down or hanging is about 30 s before the 503 `dependency-unavailable`, not a few seconds. `CatalogClient` turns Polly's `ExecutionRejectedException` (its subclasses are the timeout, the open circuit and the concurrency limiter's `RateLimiterRejectedException`: [Polly docs](https://www.pollydocs.org/api/Polly.ExecutionRejectedException.html)) into `TransientFailureException`; the endpoint answers 503 for that and for `HttpRequestException`, and logs the exception at Warning. During that wait Inventory holds no database connection or transaction: `CreateAsync` makes the Catalog call before its first use of `InventoryDbContext` (read the method), so a short pipeline as Payments has (ADR 0014) is not needed. It holds the request for up to 30 s, which is acceptable for a creator's rare action; if it were not, a shorter total timeout on this client is the first step, then the snapshot above. Editions already created are unaffected.
- **Inventory and Catalog now name their user clients** in `appsettings.json` (`Auth:UserClients: ["gateway"]`). Until now only the tests' defaults supplied it, so every gateway token would have been a 403 in Compose. `AuthSettingsTests` removes the test default and reads the configured value.
- **Ownership of a video can change in Catalog** (not possible yet). Editions keep the `CreatorId` they were created with. A transfer feature would need to decide whether editions follow the video.
