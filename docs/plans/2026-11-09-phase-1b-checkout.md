# Phase 1b: Checkout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. The `code-writer` agent (`~/.claude/agents/code-writer.md`) builds each task, and the main session reviews it before the owner sees the pull request. The tasks run in four waves, up to three at a time, each in its own git worktree and branch; see "Running tasks at once". Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A signed-in buyer buys a copy in the browser: reserve, pay on a fake provider's page, get the copy number, see it in "my copies". The 1a k6 test still passes, now through the gateway, Orders and Inventory. CI proves both.

**Architecture:** Orders runs the checkout saga. It reserves and holds a copy in Inventory with synchronous calls that carry a reservation id it chose itself, asks Payments for a payment, and reacts to Payments' events: confirm on success, release on failure, refund when a success comes after the hold ran out. Payments talks to FakePay, a small stand-in for a hosted checkout such as Stripe's, through a provider interface, and turns its signed webhooks into events, each exactly once. The YARP gateway is a backend-for-frontend: it signs the browser in to Keycloak, keeps the tokens server-side, gives the browser only an HttpOnly cookie, and forwards each API call with the user's access token. Every service validates tokens itself; calls between services carry the caller's own client-credentials token. The Angular app has four pages and never sees a token.

**Tech Stack:** as Phase 1a, plus Yarp.ReverseProxy 2.3.0, Microsoft.AspNetCore.Authentication.JwtBearer and OpenIdConnect 10.0.14, Duende.AccessTokenManagement and Duende.AccessTokenManagement.OpenIdConnect 4.3.0 (Apache-2.0), Microsoft.Extensions.Http.Resilience 10.11.0, Microsoft.AspNetCore.DataProtection.StackExchangeRedis 10.0.14, Testcontainers.Keycloak 4.16.0; Angular 22.2 on Node 26; Playwright 1.65.0.

**Spec:** [`README.md`](../../README.md): Requirements A-1, C-1, C-2, C-3, E-4 and L-1, Architecture, and the Phases table row 1b. Also [`AGENTS.md`](../../AGENTS.md). The owner approved the Phase 1b design on 2026-11-08; this plan's pull request moves its decisions into the README (Terms, Requirements, Architecture, Decisions so far), so the README is the design to argue from.

All paths below are relative to the newstore repository root, `example/newstore/`.

## What Phase 1a left open

| Input | Where it goes |
|---|---|
| Payments: module or service | A service (owner, 2026-11-08). Task 4. |
| Retries and timeouts for synchronous calls | `AddServiceClient` puts the standard resilience handler on every service-to-service client (Task 2). Only idempotent calls exist, so retries are safe (Task 7's walkthrough shows why each one is). |
| Service-to-service identity | Client credentials, one Keycloak client per calling service (owner). Task 2, ADR 0012. |
| Creator: a role or a policy | A resource rule, `sub == CreatorId` (owner). Task 6, ADR 0016. |
| Keycloak issuer | One public issuer, `http://localhost:8180/realms/newstore`, with back-channel URLs for containers. Task 2 proves it with a test. |
| E-4 criteria | This pull request adds them to the README. The design said E-4's k6 line was copied from C-4; that was wrong, because E-4 has never had criteria. |
| The gateway doesn't route `/health/*` | Task 5: the gateway lists each route explicitly, and a test checks that internal paths are not routed. |
| How Orders recovers a lost 201 | Orders picks the reservation id and retries with it (Task 3, ADR 0013). |
| #66, the EF error log | Done in PR #67. |

## Global Constraints

`AGENTS.md` binds every step. This section adds what is specific to Phase 1b.

- Package versions, added to `Directory.Packages.props` by the task that first needs them:
  - Yarp.ReverseProxy `2.3.0`
  - Microsoft.AspNetCore.Authentication.JwtBearer and Microsoft.AspNetCore.Authentication.OpenIdConnect `10.0.14`
  - Duende.AccessTokenManagement and Duende.AccessTokenManagement.OpenIdConnect `4.3.0`. Duende.BFF is commercial; don't use it.
  - Microsoft.Extensions.Http.Resilience `10.11.0`
  - Microsoft.AspNetCore.DataProtection.StackExchangeRedis `10.0.14`
  - Testcontainers.Keycloak `4.16.0`
- npm, pinned exactly in each `package.json`, with the lock file committed; install with `npm ci`:
  - `@angular/*` `22.2.5` and `@angular/cli` `22.2.6`
  - `@playwright/test` `1.65.0`
- These are project dependencies under `web/` and `tests/e2e/`, not global tools. The owner decided that Playwright's Chromium is not downloaded on the Mac at all; only CI's `e2e` job installs it.
- Images: `node:26.12.1-alpine` (it has an arm64 build) for the Angular build stage. The Phase 0 and 1a tags stay.
- Ports. The gateway breaks the "next number" rule on purpose, because it is the front door. AGENTS.md says so.

  | Port | What |
  |---|---|
  | 8100, 5100 | Gateway (Compose, `dotnet run`) |
  | 8103, 5103 | Orders |
  | 8104, 5104 | Payments |
  | 8105, 5105 | FakePay |
  | 4200 | `ng serve` |

- Memory limits: gateway, orders and payments `512m` each; fakepay `256m`. The stack stays under 12 GB.
- **ADR numbers.** They are assigned here so that parallel tasks can't collide:
  - 0008 is amended by Task 1.
  - 0012 Task 2, 0013 Task 3, 0014 Task 4, 0015 Task 5, 0016 Task 6, 0017 Task 7.
- **Sign-in.**
  - Realm `newstore`. Issuer `http://localhost:8180/realms/newstore` everywhere.
  - Each service's audience is its own name: `catalog`, `inventory`, `orders`, `payments`.
  - JwtBearer sets `MapInboundClaims = false`, so `sub` and `azp` keep their names.
- **Service calls.** Every service-to-service call goes through `AddServiceClient` (Task 2). Each call carries the caller's client-credentials token, and each callee names the clients it accepts.
- **Problems** keep 1a's rule: `type` is `urn:newstore:problem:<slug>`.
- **Data**, from 1a: IDs from `Guid.CreateVersion7()`, money `numeric(10,2)`, enums stored as strings, every "now" from `TimeProvider`.
- **Durations.**

  | Setting | Value |
  |---|---|
  | `Inventory:ReservationTtl` | 10 min (from 1a) |
  | `Inventory:HoldTtl` | 45 min |
  | `Payments:SessionLifetime` | 30 min |
  | Webhook timestamp tolerance | 5 min |
  | Transient retry pause | 1 s, doubling to a 30 s cap |

- **Local-only credentials.** Each value is the name it belongs to, so nothing needs remembering:
  - Keycloak client secrets: `gateway`, `orders` and `inventory`.
  - Sample creators: username and password are the channel slug, for example `orbital-kitchen`. The email is `<slug>@newstore.test`, and the user id is the catalog's `creatorId`.
  - FakePay API key `fakepay`, webhook secret `fakepay-webhook`.

  They appear only where AGENTS.md allows, and Task 3 adds `compose.load.yaml` to that list. Tests read client secrets from the realm file instead of repeating them.
- **The p99 rule.** Reserve through the gateway (`POST /api/orders`) keeps a p99 under 1 s on the Mac **under the steady load** (k6's `steady` scenario: a ramping arrival rate of buyers, nobody refused). The 1,000-buyers-at-once burst is the correctness test; its latency bound is soft (`BURST_P99_MS`, 3 s on the Mac). The owner changed the rule on 2026-11-13, after a diagnosis showed the burst's p99 is the time to drain 1,010 requests that arrive within 0.3 s at cold processes, which moved between 1.3 and 3.0 s with the Mac's load (summary in `tests/load/README.md`, "The rule, and what the fixes changed"). If a steady run misses 1 s, stop and bring the numbers to the main session. Never loosen the threshold.
- **The web app.** It uses no UI library in 1b, only plain CSS. Angular Material, which the README's Stack names, arrives with the first page that needs one of its components.

## Running tasks at once

| Wave | Tasks, at the same time | Owns the Compose stack on the Mac | Issue |
|---|---|---|---|
| A | 1 Messaging: transient failures ‖ 2 Sign-in groundwork | Task 2 | #68, #69 |
| B | 3 Inventory for checkout ‖ 4 Payments and FakePay ‖ 5 The gateway as BFF | Task 3 | #70, #71, #72 |
| C | 6 Creators and channels ‖ 7 Orders, the checkout saga ‖ 8 The web app | Task 6 | #73, #74, #75 |
| D | 9 Phase 1b done | Task 9 | #76 |


- A wave starts when every task of the previous wave has merged. Each task starts from that `main`, in its own worktree, on branch `phase-1/<slug>`. The slugs are `messaging-transient`, `sign-in`, `inventory-checkout`, `payments`, `gateway`, `creators`, `orders`, `web` and `done-1b`.
- **Shared files.** Tasks add lines to these and change nothing else in them:
  - `NewStore.slnx`, `Directory.Packages.props`, `compose.yaml`, `compose.load.yaml`, `deploy/compose/postgres/init.sql` and `.github/workflows/ci.yml`;
  - `AGENTS.md` (ports, credentials, Commands);
  - the README's Repository layout;
  - new files under `src/Shared/Contracts/`.
- **After each merge,** the other tasks of the wave run `git merge origin/main`, resolve the additive conflicts, build, test and push. No rebase and no force-push. The main session diffs every shared file for removed lines after such a merge (sprint 2's lesson).
- **The Compose stack.** Only the task named above runs `docker compose` on the Mac. The other tasks verify through Testcontainers, and CI's `compose-smoke` verifies their Compose changes. Every task whose project references change runs `docker compose build <service>`, which builds an image without starting the stack.
- **Docker memory.** Three Testcontainers runs share Docker. A Keycloak container takes about 700 MB and Kafka about 1 GB. Three tasks at once still fit.

## Review Focus

1. **A buyer comes back to an edition while their order is still open,** for example after closing FakePay's tab. Buy returns that open order with 200, not a 409 `already-holding` they can't act on. Test: `Buy_again_while_an_order_is_open_returns_that_order` (Task 7).
2. **An anonymous browser calls a signed-in API route.** The gateway answers 401 problem JSON, not a 302 to Keycloak that the browser's fetch can't follow. Test: `Anonymous_call_to_a_signed_in_route_is_401_not_a_redirect` (Task 5).
3. **The session can't be refreshed any more,** because Keycloak ended it or the refresh token expired. The next API call is 401 `session-expired`, the cookie is cleared, and the app shows Sign in again. Tests: `Expired_session_is_401_session_expired` (Task 5) and `Session_expired_clears_the_user` (Task 8).
4. **`returnUrl` points at another site.** The login endpoint refuses it with 400. Test: `Login_refuses_a_return_url_to_another_site` (Task 5).
5. **Redis is down.** Anonymous browsing through the gateway still works, because the session store is not needed for a request without a session. Test: `Anonymous_browsing_works_when_redis_is_down` (Task 5).

## File Structure

```
newstore/
├── compose.yaml                     + keycloak realm import and hostname, gateway, orders(+migrate), payments(+migrate), fakepay
├── compose.load.yaml                + k6 env (tokens), UserClients k6, FakePay test endpoints
├── deploy/compose/keycloak/newstore-realm.json          (Task 2)
├── deploy/compose/postgres/init.sql + orders, payments
├── src/Shared/ServiceDefaults/Security/                 (Task 2)
│   AuthOptions.cs  AuthExtensions.cs  Policies.cs  ClaimsPrincipalExtensions.cs
│   ServiceClientExtensions.cs  IServiceTokenSource.cs  ServiceTokenHandler.cs
├── src/Shared/Messaging/TransientFailureException.cs  TransientFailures.cs   (Task 1)
├── src/Shared/Contracts/Inventory/CopyAssigned.cs                             (Task 3)
├── src/Shared/Contracts/Payments/PaymentEvents.cs                             (Task 4)
├── src/Shared/Contracts/Orders/RefundRequested.cs                             (Task 4)
├── src/Gateway/                                                               (Task 5)
│   NewStore.Gateway.csproj  Program.cs  Dockerfile  appsettings*.json  Properties/launchSettings.json
│   Auth/   AuthEndpoints.cs  CsrfMiddleware.cs  GatewayAuthExtensions.cs  OidcOptions.cs  Problems.cs  RedisTicketStore.cs
│   Proxy/  ProxyExtensions.cs  AccessTokenTransform.cs
│   Redis/  RedisExtensions.cs                           the one connection and the readiness check
│   wwwroot/index.html                                   placeholder until Task 8
├── src/Services/Orders/                                                       (Task 7)
│   Orders/  Order.cs  OrderEndpoints.cs  OrderContracts.cs  OrderService.cs
│   Saga/    PaymentSucceededHandler.cs  PaymentFailedHandler.cs  PaymentRefundedHandler.cs
│   Clients/ InventoryClient.cs  PaymentsClient.cs
├── src/Services/Payments/                                                     (Task 4)
│   Payments/  PaymentAttempt.cs  PaymentEndpoints.cs  PaymentContracts.cs  WebhookEndpoint.cs
│   Refunds/   RefundRequestedHandler.cs
│   Provider/  IPaymentProvider.cs  FakePayProvider.cs  FakePaySignature.cs
├── src/Fakes/FakePay/                                                         (Task 4)
│   Sessions/  SessionStore.cs  SessionEndpoints.cs  CheckoutPage.cs  ExpirySweeper.cs
│   Webhooks/  WebhookSender.cs  WebhookSigner.cs
├── src/Services/Inventory/          + Held, hold, caller-supplied id, CopyAssigned (Task 3); owner rules, seeder (Task 6)
├── src/Services/Catalog/            + fixed video ids, /videos/{id}/owner, POST /channels (Task 6)
├── web/                             the Angular app (Task 8)
├── tests/Shared/NewStore.TestSupport/ + TestTokens.cs  HttpClientExtensions.cs  StubHttpHandler.cs (Task 2), KafkaServer.PublishAsync (Task 4)
├── tests/Security.Tests/  Gateway.Tests/  Orders.Tests/  Payments.Tests/  FakePay.Tests/
├── tests/e2e/                       Playwright (Task 9)
├── tests/load/                      + keycloak.js; last-copy.js moves to the gateway (Tasks 3, 6, 9)
└── docs/adr/0012 … 0017
```

---

### Task 1: Messaging tells "not now" from "never"

Branch `phase-1/messaging-transient`. Issue #68. Read: roadmap Stage 1 topics 10 (async/await) and 13 (resilience).

Today a handler that fails three times sends its message to `<topic>.dlt`, which nothing reads. Orders' handlers call Inventory. If Inventory were down for two minutes, a `PaymentSucceeded` would be dead-lettered, and the buyer would be charged with no copy and no refund. After this task, a transient failure is retried until it succeeds. Only a message that can never succeed goes to the dead-letter topic.

**Files:**
- Create:
  - `src/Shared/Messaging/TransientFailureException.cs` and `TransientFailures.cs`
  - `tests/Messaging.Tests/TransientFailureTests.cs`
- Modify:
  - `src/Shared/Messaging/MessagingOptions.cs`, `Kafka/KafkaConsumerService.cs` and `Telemetry/MessagingTelemetry.cs`
  - `docs/adr/0008-kafka-events.md`, by adding an "Amendment, 2026-11" section
  - `AGENTS.md`, in the Events list

**Interfaces:**
- Produces:

  ```csharp
  namespace NewStore.Messaging;
  // Thrown by a handler when a dependency is unavailable and the message should be tried again later.
  public class TransientFailureException(string message, Exception? innerException = null) : Exception(message, innerException);

  public static class TransientFailures
  {
      // True when e or any inner exception is TransientFailureException, HttpRequestException,
      // TimeoutException or a DbException whose IsTransient is true.
      public static bool IsTransient(Exception e);
  }

  // MessagingOptions
  public TimeSpan TransientRetryPause { get; set; } = TimeSpan.FromSeconds(1);
  public TimeSpan MaxTransientRetryPause { get; set; } = TimeSpan.FromSeconds(30);
  ```

- The consumer loop on a transient failure, in place of today's in-place retry:
  1. Log a warning with the topic, partition, offset, attempt and pause.
  2. `consumer.Seek(result.TopicPartitionOffset)`.
  3. Wait `min(TransientRetryPause × 2^(attempt−1), MaxTransientRetryPause)`, honouring the stop token.
  4. Keep polling. Count attempts per topic, partition and offset, store no offset, and clear the count when the message succeeds or its partition is revoked.

  The consumer keeps polling instead of sleeping in the handler loop, because librdkafka evicts a consumer that doesn't poll for `max.poll.interval.ms`. The default is 300000; [CONFIGURATION.md](https://github.com/confluentinc/librdkafka/blob/master/CONFIGURATION.md) says: "If this interval is exceeded the consumer is considered failed and the group will rebalance". Any other exception keeps today's path: `MaxHandlerAttempts` tries, then `<topic>.dlt`.
- Counter `messaging.handler.transient_retries`, tagged with `messaging.destination.name`, on the library's `Meter("NewStore.Messaging")`. Create the meter if the library has none yet.

- [x] **Step 1: Failing tests**

`TransientFailureTests`, using the existing `Scenario` and `MessagingHost` helpers, with `TransientRetryPause` 50 ms and `MaxTransientRetryPause` 200 ms:
- `Transient_failure_is_retried_until_it_succeeds_and_never_dead_lettered`: the handler throws `TransientFailureException` 5 times (more than `MaxHandlerAttempts`), then succeeds. Assert: one inbox row, one side effect, and `<topic>.dlt` stays empty for 2 s.
- `Transient_failure_holds_back_later_messages_on_the_partition`: two messages with the same key. The first fails transiently twice. Assert the handler saw them in order: A, A, A, B.
- `Stopping_during_transient_retries_leaves_the_message_for_the_next_start`: the handler always fails transiently. Stop the host after two attempts, then start a new host whose handler succeeds. Assert the message is handled once.
- `[Theory] Classifies_failures`:

  | Exception | `IsTransient` |
  |---|---|
  | `TransientFailureException` | true |
  | `HttpRequestException` | true |
  | `TimeoutException` | true |
  | `InvalidOperationException` with an inner `HttpRequestException` | true |
  | `InvalidOperationException` | false |
  | `JsonException` | false |

- The existing poisoned-message test stays as it is: a permanent failure is dead-lettered after 3 attempts.

Run `dotnet test --project tests/Messaging.Tests`. Expected: the new tests fail.

- [x] **Step 2: Implement, then run the tests**

Expected: all Messaging tests pass. Commit: `feat(messaging): retry transient handler failures without a limit`.

- [x] **Step 3: ADR 0008 amendment, AGENTS.md, pull request**

1. **ADR 0008 amendment:**
   - The rule: transient retries forever with a capped pause, permanent goes to the DLT.
   - Its cost: the whole consumer waits, because it handles one message at a time, so a down dependency stops every partition the instance holds. That is acceptable while each service consumes few topics.
   - Alternatives, each checked against its docs and linked: retry topics; DLT plus a replay tool; retrying everything forever, where a poison message blocks the partition for good.
2. **AGENTS.md, Events:** "A handler that calls another service lets `HttpRequestException` and timeouts propagate, or throws `TransientFailureException`. The consumer retries those without a limit; any other exception is dead-lettered after `MaxHandlerAttempts`."
3. **Walkthrough interview angles:** "What goes to a dead-letter topic, and who reads it?", "Why doesn't the consumer sleep inside the handler?", "What does a stuck partition cost?".

---

### Task 2: Sign-in groundwork

Branch `phase-1/sign-in`. Issue #69. Owns the Compose stack in wave A. Read: topics 09 (OAuth 2.0, OIDC and JWT) and 11 (authorization policies).

**Files:**
- **Create:**
  - `deploy/compose/keycloak/newstore-realm.json`
  - `src/Shared/ServiceDefaults/Security/*.cs`, as in File Structure
  - `tests/Shared/NewStore.TestSupport/TestTokens.cs`, `HttpClientExtensions.cs` and `StubHttpHandler.cs`
  - `tests/Security.Tests/`
  - `docs/adr/0012-service-identity-and-the-issuer.md`
- **Modify:**
  - `compose.yaml`, the keycloak service
  - `src/Shared/ServiceDefaults/NewStore.ServiceDefaults.csproj`
  - `tests/Shared/NewStore.TestSupport/NewStoreAppFactory.cs`
  - `Directory.Packages.props`: JwtBearer, Http.Resilience, Duende.AccessTokenManagement, Testcontainers.Keycloak
  - `NewStore.slnx`
  - `.github/workflows/ci.yml`: Keycloak is ready, and a token request works
  - `AGENTS.md`: the credentials list and an Architecture line

**Interfaces:**
- **The realm.**
  - **Settings:** registration allowed, no email verification, the default password policy.
  - **Clients:**
    - `gateway`: confidential, standard flow only. PKCE `S256` is required. Redirect URIs are `http://localhost:8100/signin-oidc`, `http://localhost:5100/signin-oidc` and `http://localhost:4200/signin-oidc`. The post-logout redirect URIs are the same three origins with `/`.
    - `orders` and `inventory`: confidential, service accounts only.
    - No client has direct access grants.
  - **Audience mappers:**

    | Client | Audiences |
    |---|---|
    | `gateway` | `orders`, `inventory`, `catalog` |
    | `orders` | `inventory`, `payments` |
    | `inventory` | `catalog` |

  - **Users:** the six sample creators, with the ids, usernames, passwords and emails from Global Constraints.
- **Compose keycloak:**
  - `command: ["start-dev", "--import-realm"]`
  - volume `./deploy/compose/keycloak:/opt/keycloak/data/import:ro`
  - `KC_HOSTNAME: http://localhost:8180`
  - `KC_HOSTNAME_BACKCHANNEL_DYNAMIC: "true"`

  The [Keycloak hostname guide](https://www.keycloak.org/server/hostname) says of the last option: "Set to true if your application accesses Keycloak via a private network". The browser gets front-channel URLs on `localhost:8180`, containers get back-channel URLs on `keycloak:8080`, and every token carries the one public issuer.
- **ServiceDefaults,** namespace `NewStore.ServiceDefaults.Security`:

  ```csharp
  public sealed class AuthOptions          // bound from "Auth"
  {
      public required string Issuer { get; set; }
      public string? MetadataAddress { get; set; }      // internal URL in Compose; null means Issuer + "/.well-known/openid-configuration"
      public required string Audience { get; set; }
      public bool RequireHttpsMetadata { get; set; } = true;
      public List<string> UserClients { get; set; } = []; // appsettings.json: ["gateway"]; the load overlay adds "k6" at index 1
      public string? ClientId { get; set; }             // callers only
      public string? ClientSecret { get; set; }
      public string? TokenEndpoint { get; set; }
  }

  public static class Policies
  {
      public const string SignedIn = "signed-in";       // authenticated, has sub, azp ∈ UserClients
      public static string Client(string clientId) => $"client:{clientId}";
  }

  public static class AuthExtensions
  {
      // JwtBearer: MetadataAddress, ValidIssuer = Issuer, ValidAudience = Audience, MapInboundClaims = false. Adds Policies.SignedIn.
      public static AuthorizationBuilder AddNewStoreAuth(this IHostApplicationBuilder builder);
      // Policy Client(clientId): azp == clientId. Service clients have service accounts only, and user clients have none,
      // so azp alone tells a service call from a user's.
      public static AuthorizationBuilder AddClientPolicy(this AuthorizationBuilder auth, string clientId);
  }

  public static class ClaimsPrincipalExtensions
  {
      public static Guid UserId(this ClaimsPrincipal user);     // Guid.Parse(sub)
      public static string? ClientId(this ClaimsPrincipal user);  // azp
  }

  public interface IServiceTokenSource { Task<string> GetTokenAsync(CancellationToken cancellationToken); }
  public sealed class ServiceTokenHandler(IServiceTokenSource tokens) : DelegatingHandler;   // sets Authorization: Bearer

  public static class ServiceClientExtensions
  {
      // A typed HttpClient whose BaseAddress is configuration[baseAddressKey] (for example "Services:Inventory"),
      // with AddStandardResilienceHandler() and ServiceTokenHandler inside it, so every retry carries a valid token.
      public static IHttpClientBuilder AddServiceClient<TClient>(this IHostApplicationBuilder builder, string baseAddressKey)
          where TClient : class;
  }
  ```

  The default `IServiceTokenSource` uses Duende.AccessTokenManagement client credentials, with ClientId, ClientSecret and TokenEndpoint from `Auth`. It caches the token until shortly before it expires.
- **TestSupport:**

  ```csharp
  public static class TestTokens
  {
      public const string Issuer = "http://keycloak.test/realms/newstore";
      public static SecurityKey SigningKey { get; }        // one RSA key per test run
      public static string User(Guid userId, string audience, string clientId = "gateway", TimeSpan? lifetime = null);
      public static string Client(string clientId, params string[] audiences);
  }
  public static HttpClient WithBearer(this HttpClient client, string token);   // HttpClientExtensions

  public sealed class StubHttpHandler : HttpMessageHandler
  {
      public StubHttpHandler On(HttpMethod method, string pathPattern, Func<HttpRequestMessage, HttpResponseMessage> reply);
      public IReadOnlyList<HttpRequestMessage> Requests { get; }   // with bodies already read into strings
  }
  // and a helper that makes a typed client's primary handler a given StubHttpHandler inside ConfigureTestServices
  ```

  `NewStoreAppFactory` gets these:
  - default `Auth:*` settings, with `Issuer = TestTokens.Issuer` and `UserClients:0 = gateway`;
  - a post-configure of JwtBearer with `Configuration = new OpenIdConnectConfiguration { Issuer, SigningKeys = { TestTokens.SigningKey } }`, so no metadata is fetched;
  - `IServiceTokenSource` replaced by a source that returns `TestTokens.Client(Auth:ClientId)`. Stubs don't validate it; tests assert that it is there.
- **Compose env for each service:**

  ```yaml
  Auth__Issuer: http://localhost:8180/realms/newstore
  Auth__MetadataAddress: http://keycloak:8080/realms/newstore/.well-known/openid-configuration
  Auth__Audience: <service>
  Auth__RequireHttpsMetadata: "false"
  ```

  A caller also gets `Auth__ClientId`, `Auth__ClientSecret` and `Auth__TokenEndpoint: http://keycloak:8080/realms/newstore/protocol/openid-connect/token`. Each service's own task adds these lines. `appsettings.Development.json` uses `localhost:8180` for all three URLs.

- [x] **Step 1: The realm and Compose**

Write the realm, change the keycloak service, then run `docker compose up -d --wait keycloak`. Check:
- `curl -s http://localhost:8180/realms/newstore/.well-known/openid-configuration | jq -r .issuer` prints `http://localhost:8180/realms/newstore`.
- A client-credentials request for `orders` returns a token whose `aud` contains `inventory` and `payments`.

Commit: `feat(keycloak): import the newstore realm with its clients and sample creators`.

- [x] **Step 2: Failing tests in `tests/Security.Tests`**

These tests run against Keycloak in Testcontainers, with the realm imported and the public hostname set to `http://public.newstore.test:8180`:
- `Token_from_the_internal_url_carries_the_public_issuer`: get an `orders` client-credentials token from the container's mapped URL. Assert:
  - `iss` is `http://public.newstore.test:8180/realms/newstore`;
  - `aud` contains `inventory` and `payments`;
  - `azp` is `orders`.
- `A_service_validates_a_keycloak_token_with_the_public_issuer`: build a minimal `WebApplication` in the test with `AddNewStoreAuth` and the settings `Issuer` public, `MetadataAddress` internal, `Audience` `inventory`, plus one endpoint that requires `Client("orders")`. The `orders` token gets 200. A token for audience `catalog` gets 401.
- `Sample_creator_has_their_catalog_id`: through the admin API, user `orbital-kitchen` has id `00000000-0000-7000-8000-000000000001`.
- `Our_clients_have_no_password_grant`: a password grant with client `gateway` returns 400 `unauthorized_client`.

These use `TestTokens` and the same minimal app:
- `SignedIn_rejects_a_service_token`: 403.
- `SignedIn_accepts_a_user_token_from_the_gateway`: 200.
- `SignedIn_rejects_a_user_token_from_an_unknown_client`: 403.
- `Client_policy_accepts_only_the_named_client`: `orders` gets 200, `inventory` gets 403.
- `Wrong_audience_is_401`.
- `Expired_token_is_401`.

Tests read client secrets from `deploy/compose/keycloak/newstore-realm.json`. Expected: they fail to compile, because `AddNewStoreAuth` doesn't exist yet.

- [x] **Step 3: Implement ServiceDefaults security and TestSupport, then run the tests**

Also add `ServiceClientTests` in Security.Tests:
- `Service_client_sends_a_token_on_every_retry`: a `StubHttpHandler` answers 503 and then 200. Both requests carry `Authorization: Bearer`.
- `Service_client_reads_its_base_address_from_configuration`.

Before building the order of handlers, check how `AddStandardResilienceHandler` and `AddHttpMessageHandler` nest in the [Http.Resilience docs](https://learn.microsoft.com/dotnet/core/resilience/http-resilience). Quote the sentence in the walkthrough. Expected: Security.Tests pass, and every existing test still passes.

Commit: `feat(security): token validation, client policies and service clients for every service`.

- [x] **Step 4: ADR 0012, CI, AGENTS.md, pull request**

1. **ADR 0012, service identity and the issuer.**
   - The decision: client credentials per calling service, and the callee names the clients it accepts.
   - Alternatives, each checked and linked:
     - forwarding the user's token, where every service would trust every caller with the user's power;
     - mTLS;
     - trusting the Docker network, an anti-goal;
     - token exchange (RFC 8693).
   - The issuer setup, quoting the Keycloak guide.
2. **CI `compose-smoke`:** after the stack starts, a client-credentials request for `orders` returns 200. Read the secret from the realm file with `jq`.
3. **AGENTS.md:**
   - In Local stack, the credentials line names client secrets, sample users and the FakePay key and secret as local-only. Task 4 introduces FakePay's.
   - In Architecture rules: "A service validates tokens with `AddNewStoreAuth` and calls another service only through `AddServiceClient`."
4. **Walkthrough, the deep one for sign-in.** Interview angles:
   - "Why can't a service trust a call because it comes from inside the network?"
   - "What do `aud` and `azp` each tell you?"
   - "What is the client credentials grant for, and what is it not for?"

---

### Task 3: Inventory for checkout

Branch `phase-1/inventory-checkout`. Issue #70. Owns the Compose stack in wave B. Read: topic 08 (sagas). The hard part "reservations and copy numbers" grows here: walk through the hold and the caller-supplied id step by step.

**Files:**
- **Modify in Inventory:**
  - `Reservations/`: `Reservation.cs`, `ReservationContracts.cs`, `IReserveStrategy.cs`, `ReserveOutcome.cs`, `ReserveSupport.cs`, both strategies, `ReservationService.cs`, `ReservationExpiryService.cs`, `ReservationEndpoints.cs` and `HandledConflictLoggerFactory.cs`
  - `InventoryOptions.cs`, `InventoryDbContext.cs`, `Program.cs`, `Problems.cs`, `appsettings*.json`
  - a migration, `AddHeldReservations`
- **Create:**
  - `src/Shared/Contracts/Inventory/CopyAssigned.cs`
  - `docs/adr/0013-reservations-for-checkout.md`
- **Modify elsewhere:**
  - `tests/Inventory.Tests/*`: reservation calls now carry `TestTokens.Client("orders", "inventory")` and a `reservationId`
  - `tests/load/last-copy.js` and `tests/load/README.md`
  - `compose.yaml`: inventory's `Auth__*` env
  - `compose.load.yaml`: the k6 env for the token
  - `AGENTS.md`: `compose.load.yaml` joins the files that may hold local credentials

**Interfaces:**
- **The model:**
  - `ReservationStatus` gets `Held`: `{ Pending, Held, Confirmed, Expired, Released }`.
  - `Reservation.ExpiresAt` becomes `{ get; set; }`.
  - New property `public DateTimeOffset? HeldAt { get; set; }`.
  - `InventoryOptions.HoldTtl` defaults to `TimeSpan.FromMinutes(45)`.
  - The partial unique index filter becomes `status IN ('Pending', 'Held', 'Confirmed')`, and the sweep's `WHERE` becomes `status IN ('Pending', 'Held')`.
- **The reserve contract:**

  ```csharp
  public sealed record ReserveRequest(Guid ReservationId, Guid BuyerId);   // buyerId in the body: the caller is the trusted client `orders`
  public sealed record ReservationResponse(Guid Id, Guid EditionId, Guid BuyerId, Guid VideoId, string Format, decimal Price,
      string Status, DateTimeOffset ReservedAt, DateTimeOffset ExpiresAt, int? CopyNumber);

  Task<ReserveOutcome> ReserveAsync(Guid reservationId, Guid editionId, Guid buyerId, CancellationToken cancellationToken);  // IReserveStrategy
  // ReserveOutcome gains:
  public sealed record Existing(Reservation Reservation) : ReserveOutcome;   // 200
  public sealed record IdTaken : ReserveOutcome;                             // 409 reservation-id-taken
  ```

  1a's note that buyerId would come from the token no longer applies. Only `orders` may call reserve, and it passes the signed-in buyer's `sub`.
- **The caller-supplied id.** Both strategies follow the same algorithm, written once in `ReserveSupport`:
  1. Before taking a copy, look the id up. If it exists with the same edition and buyer, return `Existing`; otherwise return `IdTaken`.
  2. On a `23505` after the rollback, look the id up again. If it now exists with the same edition and buyer, return `Existing` (a parallel request with the same id won). If it exists with another edition or buyer, return `IdTaken`. If it doesn't exist, return `AlreadyHolding`.

  `ReserveSupport.IsReservationConflict(DbUpdateException)` replaces `IsAlreadyHolding` and matches any `23505` on `reservations`. `HandledConflictLoggerFactory` uses it, so both handled conflicts stay out of the EF error log.
- **Hold.** `POST /reservations/{id}/hold` calls `ReservationService.HoldAsync(Guid id, CancellationToken) → HoldOutcome`, where `HoldOutcome` is `Ok(Reservation)`, `NotFound`, `Expired` or `Released`. It locks the row as confirm does.

  | Reservation | Result |
  |---|---|
  | `Pending`, `ExpiresAt > now` | `Held`, `HeldAt = now`, `ExpiresAt = now + HoldTtl`; 200 |
  | `Held` or `Confirmed` | 200 unchanged; a repeated hold doesn't extend |
  | `Pending`, `ExpiresAt <= now` | expire it and return the copy, as confirm does; 409 `reservation-expired` |
  | `Expired` | 409 `reservation-expired` |
  | `Released` | 409 `reservation-released` |

- **Confirm and release.** Confirm accepts `Pending` or `Held` before `ExpiresAt`. Release covers `Pending` and `Held`.
- **`CopyAssigned`,** in `NewStore.Contracts.Inventory`:
  - `Topic = "inventory.copies"`, `Partitions = 3`;
  - fields `EventId`, `OccurredAt`, `ReservationId`, `EditionId`, `VideoId`, `Format`, `OwnerId` and `CopyNumber`;
  - key `EditionId`;
  - enqueued in confirm's transaction, so the first confirm publishes it and a repeated confirm doesn't.
- **Authorization.**
  - The reservation endpoints (reserve, get, hold, confirm, release) require `Policies.Client("orders")`. `Auth:Audience` is `inventory`.
  - The edition endpoints stay open until Task 6.

- [x] **Step 1: Failing tests**

In `ReservationTests` and a new `HoldTests`:
- `Reserve_twice_with_the_same_id_returns_the_same_reservation`: the first call is 201, the second 200 with the same id, and `copiesLeft` drops by one.
- `Parallel_reserves_with_the_same_id_take_one_copy`: 20 parallel calls with one id give one reservation, one copy taken, and no EF Error log record (use `LogCapture`).
- `Reusing_an_id_for_another_edition_is_409`: 409 `reservation-id-taken`.
- `Hold_keeps_the_copy_past_ten_minutes`: hold, move time 11 minutes, sweep. The reservation is still `Held`, and confirm gives a copy number.
- `Held_reservation_expires_at_the_hard_limit`: hold, move time 45 minutes plus 1 second, sweep. It is `Expired`, the copy is back, and confirm is 409.
- `Hold_twice_does_not_extend`: the second hold returns the same `expiresAt`.
- `Hold_after_ten_minutes_is_409_expired`: the copy is back on sale.
- `Hold_of_a_released_reservation_is_409_released`.
- `Release_of_a_held_reservation_returns_the_copy`.
- `Buyer_with_a_held_reservation_cannot_reserve_again`: 409 `already-holding`.
- `Confirm_publishes_CopyAssigned_once`: confirm twice gives one `outbox_messages` row of type `CopyAssigned`, with the copy number.
- `Reservation_endpoints_accept_only_the_orders_client`: no token is 401; `inventory`'s client token is 403; a user token is 403.
- `Reserve_response_carries_the_price_format_and_video`.

Run `dotnet test --project tests/Inventory.Tests`. Expected: the new tests fail, and the old tests fail on the changed request shape until Step 2.

- [x] **Step 2: Implement, migrate, run the tests**

Generate the migration with the AGENTS.md command, and check that it changes the index filter and adds `held_at`. Expected: Inventory.Tests pass. Commit: `feat(inventory): hold a reservation while the buyer pays, with a caller-supplied id`.

- [x] **Step 3: k6 with a service token, ADR 0013, pull request**

1. **`last-copy.js`:** at setup, get an `orders` client-credentials token from `http://keycloak:8080/realms/newstore/protocol/openid-connect/token`. The client secret comes from `compose.load.yaml`'s k6 env (`ORDERS_CLIENT_SECRET: orders`). Each reserve body gets `reservationId: uuidv4()`.
2. **Run it:** `docker compose -f compose.yaml -f compose.load.yaml up -d --wait`, then `… run --rm k6`. Expected: green, with p99 under 1 s. Record the numbers in `tests/load/README.md`.
3. **ADR 0013, reservations for checkout:**
   - the hold and its 45-minute limit owned by Inventory, with the Stripe `expires_at` range quoted from the [Stripe API](https://docs.stripe.com/api/checkout/sessions/create);
   - the caller-supplied id as the fix for a lost 201;
   - alternatives: Orders extending a timer; an `Idempotency-Key` header with a stored response.
4. **Walkthrough, deep.** Interview angles:
   - "How do you make a POST safe to retry?"
   - "What happens if the buyer pays at minute 46?"
   - "Why is the hold limit in Inventory and not in Orders?"

---

### Task 4: Payments and FakePay

Branch `phase-1/payments`. Issue #71. Read: topic 08 (sagas: compensation). The hard part "idempotent consumers" gets a second example here, the webhook. Walk through it step by step.

**Files:**
- **Create:**
  - `src/Services/Payments/` and `src/Fakes/FakePay/`, as in File Structure. Each has its csproj, Program, appsettings, launchSettings (5104 and 5105) and Dockerfile.
  - `src/Shared/Contracts/Payments/PaymentEvents.cs` and `src/Shared/Contracts/Orders/RefundRequested.cs`
  - `tests/Payments.Tests/` and `tests/FakePay.Tests/`
  - `docs/adr/0014-payments-and-the-fake-provider.md`
- **Modify, adding lines only:**
  - `NewStore.slnx`
  - `compose.yaml`: `payments`, `payments-migrate`, `fakepay`
  - `init.sql`: the `payments` role and database
  - `tests/Shared/NewStore.TestSupport/KafkaServer.cs`: `PublishAsync`
  - `ci.yml`: Payments and FakePay are ready
  - `AGENTS.md`: ports, the FakePay credentials, and the shared-topic rule

**Interfaces:**
- **Contracts.** One topic per producer, so a payment's events stay in order per order:

  ```csharp
  namespace NewStore.Contracts.Payments;
  public static class PaymentEvents { public const string Topic = "payments.payments"; public const int Partitions = 3; }   // key: OrderId
  public sealed record PaymentSucceeded : IIntegrationEvent { EventId, OccurredAt, PaymentId, OrderId, decimal Amount }
  public sealed record PaymentFailed    : IIntegrationEvent { EventId, OccurredAt, PaymentId, OrderId, string Reason }
  public sealed record PaymentRefunded  : IIntegrationEvent { EventId, OccurredAt, PaymentId, OrderId, decimal Amount }

  namespace NewStore.Contracts.Orders;
  public static class OrderEvents { public const string Topic = "orders.orders"; public const int Partitions = 3; }   // key: OrderId
  public sealed record RefundRequested  : IIntegrationEvent { EventId, OccurredAt, OrderId, PaymentId }
  ```

  The AGENTS.md rule grows a line: when several events share a topic, its name and partition count live in one static class named for the producer.
- **TestSupport:** `KafkaServer.PublishAsync<TEvent>(string topic, string key, TEvent @event, CancellationToken)`. It sets the `type` header the way `MessageHeaders` does, so a service's consumer accepts the message.
- **Payments,** database `payments`, with the migrate job, inbox and outbox:

  ```csharp
  public enum PaymentStatus { Created, Succeeded, Failed, Refunded }
  public sealed class PaymentAttempt { Id; OrderId (unique); decimal Amount; PaymentStatus Status; string? ProviderSessionId;
      string? CheckoutUrl; DateTimeOffset? SessionExpiresAt; DateTimeOffset CreatedAt; DateTimeOffset UpdatedAt; uint Version (xmin) }
  // table provider_events (event_id text primary key, received_at)

  public interface IPaymentProvider
  {
      Task<ProviderSession> CreateSessionAsync(PaymentAttempt attempt, string successUrl, string cancelUrl, CancellationToken ct);
      Task RefundAsync(PaymentAttempt attempt, CancellationToken ct);
      ProviderEvent? ReadWebhook(string body, IHeaderDictionary headers);   // null when the signature or timestamp is bad
  }
  public sealed record ProviderSession(string SessionId, string CheckoutUrl, DateTimeOffset ExpiresAt);
  public enum ProviderEventType { SessionCompleted, SessionExpired }
  public sealed record ProviderEvent(string EventId, ProviderEventType Type, string SessionId);
  ```

  - **Configuration:** `Payments:Provider:BaseUrl`, `ApiKey` and `WebhookSecret`, plus `Payments:SessionLifetime` (30 min).
  - **`POST /payments`** with `{ orderId, amount, returnUrl }`, `Client("orders")`:
    - It inserts a `Created` attempt, then calls `CreateSessionAsync`, whose idempotency key is the attempt id, and stores the session. That is a 201 with `PaymentResponse`.
    - When an attempt exists for the order, it returns that attempt with 200. If that attempt has no session yet, it creates one first with the same key.
    - Two parallel creates meet at the unique `order_id`. The loser reloads the attempt and continues as "exists".
    - The provider being down gives 503 `dependency-unavailable` with `Retry-After`.
  - **`GET /payments/{id}`,** `Client("orders")`: `PaymentResponse(Id, OrderId, Amount, Status, CheckoutUrl, SessionExpiresAt)`.
  - **`POST /webhooks/fakepay`,** anonymous; the signature is the check:
    1. `ReadWebhook` returns null, so the endpoint answers 400 and changes nothing.
    2. The event id is already in `provider_events`, so the endpoint answers 200 and changes nothing.
    3. Otherwise, in one transaction: insert the event id, then make a guarded move from `Created` only. A completed session moves to `Succeeded` and enqueues `PaymentSucceeded`. An expired session moves to `Failed` and enqueues `PaymentFailed { Reason = "expired" }`.
    4. If the attempt is in any other state, nothing moves.
    5. An unknown session gets 200 and an Information log line.
  - **`RefundRequestedHandler`:**
    - `Succeeded`: `RefundAsync` with idempotency key = payment id, then `Refunded`, then enqueue `PaymentRefunded`.
    - `Refunded`: nothing.
    - Anything else: `InvalidOperationException`, so the message is dead-lettered.

    A provider that is down raises `HttpRequestException`, which Task 1 retries.
- **FakePay** is not a store service: no database and no outbox, and its state lives in memory. Options, bound from `FakePay`:

  ```csharp
  public sealed class FakePayOptions { ApiKey; WebhookSecret; WebhookUrl; PublicBaseUrl; bool DeliverTwice = false;
      bool EnableTestEndpoints = false; TimeSpan WebhookRetryInterval = 2 s; TimeSpan ExpirySweepInterval = 5 s; }
  ```

  - **`POST /v1/checkout/sessions`:**
    - Headers: `Authorization: Bearer <ApiKey>`, otherwise 401, and `Idempotency-Key`: the same key returns the same session.
    - Body: `{ amount, successUrl, cancelUrl, expiresAt, metadata }`. It is a 400 unless `expiresAt` is 30 minutes to 24 hours ahead, as Stripe's range.
    - Reply: `{ id, url = PublicBaseUrl + "/checkout/" + id, status = "open", expiresAt }`.
  - **`GET /checkout/{id}`:** an HTML page with the amount and two buttons, `data-testid="fakepay-pay"` and `fakepay-decline`.
    - Pay completes the session, answers 303 to `successUrl`, and queues a `checkout.session.completed` webhook.
    - Decline shows `data-testid="fakepay-error"` and leaves the session open.
    - The page answers 409 for a session that is completed or expired.
  - **Webhooks.**
    - Body: `{ id: "evt_…", type, data: { sessionId } }`.
    - Header: `FakePay-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(WebhookSecret, "{t}.{body}")>`.
    - Delivery is retried every `WebhookRetryInterval` until a 2xx. With `DeliverTwice`, each event is sent twice.
  - **Expiry.** The sweeper expires open sessions past `expiresAt` and queues `checkout.session.expired`. Paying an expired session is 409.
  - **`POST /v1/refunds`** with `{ sessionId }` and an `Idempotency-Key`. It is a 409 unless the session is completed. A repeated call returns the same refund.
  - **`POST /test/sessions/{id}/pay`** pays without the page. It exists only when `EnableTestEndpoints` is set; otherwise the path is a 404.
- **Compose:**
  - fakepay sets `PublicBaseUrl` to `http://localhost:8105` and `WebhookUrl` to `http://payments:8080/webhooks/fakepay`;
  - payments sets `Payments__Provider__BaseUrl: http://fakepay:8080`;
  - payments' `Auth__Audience` is `payments`.

- [x] **Step 1: FakePay, test-first** (done in the other order: the tests were written after the code. The PR shows three mutation runs instead: dropping `DeliverTwice`, dropping the webhook retry and stopping the sweeper each fail one FakePay test.)

Write `FakePay.Tests`:
- `Same_idempotency_key_returns_the_same_session`
- `Session_lifetime_outside_30_minutes_to_24_hours_is_400`
- `Missing_or_wrong_api_key_is_401`
- `Pay_redirects_to_the_success_url_and_sends_a_signed_completed_webhook`: verify the signature in the test.
- `Decline_shows_an_error_and_keeps_the_session_open`
- `Webhook_is_retried_until_it_gets_a_2xx`
- `DeliverTwice_sends_each_webhook_twice`
- `Open_session_expires_and_sends_an_expired_webhook`
- `Paying_a_completed_session_again_is_409`
- `Refund_is_idempotent`
- `Refund_of_an_open_session_is_409`
- `Test_pay_endpoint_is_404_unless_enabled`

The webhook receiver in these tests is a `StubHttpHandler` behind FakePay's typed client.

Then implement. Before you write the Decline behaviour into ADR 0014, check it in Stripe's docs. If no page states it, record that it couldn't be confirmed. Expected: FakePay.Tests pass. Commit: `feat(fakepay): a hosted-checkout stand-in with signed webhooks`.

- [x] **Step 2: Payments, failing tests**

`Payments.Tests` uses PostgreSQL and Kafka from TestSupport and a `StubHttpHandler` for FakePay. It must not reference the FakePay project, because two web projects in one test project give two generated `Program` classes. Sign webhooks with a small signer in the test.

Tests:
- `Create_payment_returns_the_checkout_url`
- `Second_create_for_the_same_order_returns_the_first_payment`
- `Parallel_creates_for_one_order_make_one_payment`: 10 at once give one attempt and one provider session request.
- `Only_the_orders_client_may_create_a_payment`
- `Signed_completed_webhook_publishes_PaymentSucceeded_once`: there is one `outbox_messages` row.
- `Duplicate_webhook_publishes_nothing_new`
- `Expired_after_completed_changes_nothing`
- `Webhook_with_a_bad_signature_is_400_and_changes_nothing`
- `Webhook_older_than_five_minutes_is_400`
- `RefundRequested_refunds_once_and_publishes_PaymentRefunded`: publish the event twice with `KafkaServer.PublishAsync`. Assert one refund request and one `PaymentRefunded`.
- `Refund_waits_while_the_provider_is_down`: the stub answers 503 three times, then 200. Assert the event isn't dead-lettered and the attempt ends `Refunded`.

Expected: the tests fail.

- [x] **Step 3: Implement Payments, migrate, run the tests**

Expected: Payments.Tests and FakePay.Tests pass, and `docker compose build payments fakepay` succeeds. Commit: `feat(payments): payment attempts, the FakePay webhook and refunds`.

- [x] **Step 4: ADR 0014, Compose, CI, pull request**

1. **ADR 0014, Payments and the fake provider.**
   - Why a separate service.
   - Why the provider sits behind an interface: Phase 7 swaps in Stripe.
   - Webhook signing and the replay window, compared with [Stripe's webhook signatures](https://docs.stripe.com/webhooks#verify-events) and linked.
   - Why a refund is driven by an event and not by a call.
2. **Compose:** the services from Files, with memory limits, `Messaging__BootstrapServers`, and a health check like the others. CI `compose-smoke` checks that both are ready.
3. **Walkthrough, deep for the webhook.** Interview angles:
   - "How do you make a webhook handler idempotent?"
   - "Why check the timestamp as well as the signature?"
   - "What does an idempotency key on the provider call protect against?"

---

### Task 5: The gateway as BFF

Branch `phase-1/gateway`. Issue #72. Read: topics 09 (OAuth 2.0 and OIDC: browser-based apps) and 12 (YARP). The browser sign-in path gets the deep walkthrough (owner's decision 6).

**Files:**
- **Create:**
  - `src/Gateway/`, as in File Structure, with `NewStore.Gateway.csproj` (Web SDK)
  - `tests/Gateway.Tests/`
  - `docs/adr/0015-the-gateway-is-the-bff.md`
- **Modify, adding lines only:**
  - `Directory.Packages.props`: YARP, OpenIdConnect, Duende OIDC, DataProtection.StackExchangeRedis
  - `NewStore.slnx` and `compose.yaml` (`gateway`)
  - `ci.yml`: the gateway is ready, and search through it
  - `AGENTS.md`: ports 8100 and 5100, with the front-door note
  - the README layout

**Interfaces:**
- **Configuration:**
  - `Oidc:Authority`, the public issuer;
  - `Oidc:MetadataAddress`, the internal one in Compose;
  - `Oidc:ClientId` and `Oidc:ClientSecret`, both `gateway`;
  - `Auth:*` as in Task 2, for bearer requests;
  - `ConnectionStrings:Redis`;
  - `Services:Catalog`, `Services:Inventory` and `Services:Orders`, the YARP destinations.
- **Authentication:**
  - **The cookie** `newstore-session` is HttpOnly and `SameSite=Strict` (owner's decision, 2026-11-13, following RFC 10017's "SHOULD" for the BFF's cookie). It has no `Secure` flag and no `__Host-` prefix until Phase 4 brings HTTPS; the ADR says so.
  - **Ticket store.** The session ticket lives in Redis through `RedisTicketStore : ITicketStore`, so the cookie carries only a key. A Redis failure while reading a ticket is logged, and the request counts as anonymous.
  - **Data protection.** Keys are in Redis (`PersistKeysToStackExchangeRedis`) with `SetApplicationName("newstore-gateway")`, so a restart or a second instance reads the same cookies.
  - **OIDC:**
    - code flow with PKCE;
    - `SaveTokens = true` and `MapInboundClaims = false`;
    - scopes `openid profile`;
    - Duende `AddOpenIdConnectAccessTokenManagement()`, which refreshes the access token before it expires.
  - **The policy scheme.** A request with `Authorization: Bearer …` goes to JwtBearer, and anything else goes to the cookie. JwtBearer checks issuer, signature, lifetime and `azp ∈ Auth:UserClients`, but not the audience. A comment says why: the gateway doesn't consume the token, and each service checks its own audience.
  - **Challenges.** A challenge on `/api/*` is a 401 problem, never a redirect. `/auth/user` never challenges: it answers `isAuthenticated: false`.
- **The BFF endpoints:**
  - **`GET /auth/login?returnUrl=`** starts the OIDC challenge. `returnUrl` must be a local path: it starts with `/`, and doesn't start with `//` or `/\`. Otherwise it is a 400.
  - **`GET /auth/user`** returns `{ isAuthenticated: false }`, or `{ isAuthenticated: true, sub, name, logoutUrl: "/auth/logout?sid=<sid>" }`. It never returns tokens.
  - **`GET /auth/logout?sid=`** checks that `sid` equals the session's `sid` claim, otherwise 400, so another site can't sign the user out. It then signs out of the cookie and of Keycloak, and returns to `/`.
- **CSRF.** A cookie-authenticated request to `/api/*` or `/auth/user` without the header `X-CSRF: 1` is a 403 problem `csrf` and isn't forwarded. Bearer and anonymous requests need no header.
- **`AccessTokenTransform`:**
  - A cookie session gets `Authorization: Bearer <user access token>`, read through Duende's `GetUserAccessTokenAsync`.
  - A bearer request goes as it is.
  - An anonymous request goes without a token.
  - The `Cookie` header is always removed before forwarding.
  - When the token can't be refreshed, the gateway signs the session out and answers 401 problem `session-expired`.
- **Routes.** Every route is listed explicitly, with `PathRemovePrefix("/api")`:

  | Method and path | Destination | Policy |
  |---|---|---|
  | GET `/api/videos`, `/api/videos/{id}`, `/api/videos/{id}/editions`, `/api/channels/{slug}` | catalog | anonymous |
  | POST `/api/channels` | catalog | signed-in |
  | POST `/api/editions` | inventory | signed-in |
  | GET `/api/editions/{id}` and `/api/editions/{id}/copies` | inventory | signed-in |
  | PATCH `/api/editions/{id}` | inventory | signed-in |
  | POST `/api/editions/{id}/end-sale` | inventory | signed-in |
  | POST `/api/orders`, GET `/api/orders/{id}`, POST `/api/orders/{id}/pay`, GET `/api/me/copies` | orders | signed-in |

  The gateway's signed-in policy means authenticated, with `sub`. The services hold every business rule.
- **Static files.** The gateway serves `wwwroot` and calls `MapFallbackToFile("index.html")` for paths outside `/api`, `/auth`, `/signin-oidc`, `/signout-callback-oidc` and `/health`. An unknown `/api/*` path gets a 404 problem.
- **Health.** The Redis check is tagged ready, with `failureStatus: Degraded`, as Catalog's is.

- [x] **Step 1: Failing tests in `tests/Gateway.Tests`**

**Fixtures:**
- Keycloak in Testcontainers with the realm imported and no hostname set, so the test and the gateway both reach it at the mapped URL and the issuer is that URL.
- Redis in Testcontainers.
- Destinations through a replaced `IForwarderHttpClientFactory` whose client wraps a `StubHttpHandler`, so tests see exactly what was forwarded.
- The gateway runs in the test server at `http://localhost`, so the fixture adds `http://localhost/signin-oidc` and `http://localhost/` to client `gateway`'s redirect URIs through the admin API.
- A scripted login helper: follow `/auth/login` to Keycloak's form, read the form's `action`, post the user `orbital-kitchen`, and follow the redirects back. Gateway URLs go through the factory's client and Keycloak URLs through a real `HttpClient`, so follow redirects by hand, with one cookie container for both.

**Tests:**
- `Sign_in_sets_only_an_httponly_session_cookie`: the browser holds one cookie, which is HttpOnly. No response body or header contains the access token.
- `Signed_in_api_call_reaches_the_service_with_the_users_access_token`: the forwarded request has a bearer token whose `sub` is the user's id, and no `Cookie` header.
- `Cookie_request_without_the_csrf_header_is_403_and_not_forwarded`
- `Bearer_request_is_forwarded_as_is_without_csrf`
- `Anonymous_read_is_forwarded_without_a_token`
- `Anonymous_call_to_a_signed_in_route_is_401_not_a_redirect` (Review Focus 2).
- `Login_refuses_a_return_url_to_another_site`: `https://evil.example`, `//evil.example` and `/\evil.example` each give 400 (Review Focus 4).
- `Logout_needs_the_session_id`: a missing or wrong `sid` gives 400, and the session stays.
- `Internal_paths_are_not_routed`: each of these is a 404 and is not forwarded:
  - `/api/health/ready`
  - `/api/openapi/v1.json`
  - `/api/editions/{id}/reservations`
  - `/api/reservations/{id}/confirm`
  - `/api/videos/{id}/owner`
- `Expired_session_is_401_session_expired` (Review Focus 3): sign in, then end the user's sessions through Keycloak's admin API. Set Duende's refresh-before-expiry option above the token lifetime in this test, so the next call must refresh; check the option's name in Duende's docs. Then the API call is 401 `session-expired`, and `/auth/user` says not authenticated.
- `Anonymous_browsing_works_when_redis_is_down` (Review Focus 5): stop Redis. `GET /api/videos` is forwarded with 200, and `/health/ready` is 200 `Degraded`.
- `Session_survives_a_gateway_restart`: sign in on one factory, dispose it, start a new factory on the same Redis. `/auth/user` is authenticated with the old cookie.
- `Unknown_non_api_path_serves_the_app`: `/videos/123` returns `index.html`.
- `Unknown_api_path_is_404_problem_not_the_app`

Expected: the tests fail.

- [x] **Step 2: Implement, then run the tests**

Expected: Gateway.Tests pass, and `docker compose build gateway` succeeds. Commit: `feat(gateway): YARP as a backend-for-frontend with Keycloak sign-in`.

- [x] **Step 3: ADR 0015, Compose, CI, pull request**

1. **ADR 0015, the gateway is the BFF.**
   - RFC 10017 (BCP 212) lists three browser patterns "in decreasing order of security" and says BFF is "strongly recommended for business applications". Quote it from the [datatracker](https://datatracker.ietf.org/doc/draft-ietf-oauth-browser-based-apps/).
   - Alternatives: the SPA holding tokens with PKCE, which was the README's old design; a token-mediating backend.
   - Costs: a session store, CSRF, and sticky state in Redis.
   - Why `SameSite=Strict` plus a custom header, and why the cookie isn't `Secure` until Phase 4.
2. **Compose `gateway`:**
   - `127.0.0.1:8100:8080`, `512m`;
   - depends on keycloak, redis, catalog and inventory; orders is added by Task 7;
   - the `Oidc__*` and `Auth__*` env, and `Services__*` with `http://<service>:8080`.
3. **CI `compose-smoke`:** the gateway is ready, and `curl --fail 'http://localhost:8100/api/videos?q=sourdough' | grep -q '"total":1'`.
4. **Walkthrough, deep: one sign-in, request by request.** Interview angles:
   - "Why shouldn't a browser app hold an access token?"
   - "What does SameSite protect against, and what doesn't it?"
   - "How does the BFF stop CSRF?"
   - "What's the difference between this BFF and 'a backend per client type'?"

---

### Task 6: Creators and channels

Branch `phase-1/creators`. Issue #73. Owns the Compose stack in wave C. Read: topic 11 (authorization: resource-based handlers). Advances A-1 (#1): a viewer becomes a creator by opening a channel.

**Files:**
- **Catalog:**
  - `Seed/sample-catalog.json`, adding the fixed video ids
  - `Seed/CatalogSeeder.cs`
  - `Channels/ChannelEndpoints.cs` and `ChannelContracts.cs`
  - `Videos/VideoEndpoints.cs`, for the owner endpoint
  - `Program.cs`, `appsettings*.json`
  - a migration, `UniqueChannelCreator`
- **Inventory:**
  - `Editions/EditionContracts.cs` and `EditionEndpoints.cs`
  - `Editions/EditionOwnerHandler.cs` and `Clients/CatalogClient.cs`, both new
  - `Seed/InventorySeeder.cs` and `Seed/sample-editions.json`, both new
  - `Program.cs`
- **Tests:** `tests/Catalog.Tests` and `tests/Inventory.Tests`. The edition tests now use `TestTokens.User`.
- **Load test:**
  - `tests/load/keycloak.js`, new
  - `tests/load/last-copy.js`
  - `compose.load.yaml`
- **Compose:** inventory-migrate's `command` becomes `["migrate", "seed"]`; Auth env for catalog; inventory's caller credentials.
- **Docs:** `docs/adr/0016-creator-is-a-resource-rule.md`.

**Interfaces:**
- **Fixed sample video ids:** `00000000-0000-7000-8001-0000000000CK`, where C is the creator's digit 1–6 and K is the video's position 1–8 in its channel. "Sourdough in Zero Gravity" is `00000000-0000-7000-8001-000000000011`. A Mac stack seeded before this change needs one `docker compose down -v`; the walkthrough says so.
- **Catalog:**

  ```csharp
  public sealed record VideoOwner(Guid VideoId, Guid CreatorId);                // GET /videos/{id}/owner, Client("inventory"); 404 if unknown
  public sealed record CreateChannelRequest(string Name, string Slug, string? Description);   // POST /channels, SignedIn
  public sealed record ChannelResponse(Guid Id, string Name, string Slug, string? Description);
  ```

  `POST /channels` takes the creator from `User.UserId()`. Responses:
  - 201 with `Location: /channels/{slug}`;
  - 409 `channel-exists` when the user already has one (a unique index on `channels.creator_id`);
  - 409 `slug-taken`;
  - 400 when the name isn't 1–80 characters or the slug doesn't match `^[a-z0-9-]{3,40}$`.

  `Auth:Audience` is `catalog`.
- **Inventory:**

  ```csharp
  public sealed record CreateEditionRequest(Guid VideoId, string? Format, decimal Price, int Copies);   // CreatorId comes from the token
  public sealed class CatalogClient(HttpClient http) { public Task<Guid?> GetVideoOwnerAsync(Guid videoId, CancellationToken ct); }  // null on 404
  public sealed class EditionOwnerRequirement : IAuthorizationRequirement;
  public sealed class EditionOwnerHandler : AuthorizationHandler<EditionOwnerRequirement, Edition>;   // sub == edition.CreatorId
  ```

  - **Create** requires `SignedIn`.
    - An unknown video is a 400 validation problem on `videoId`.
    - Another creator's video is a 403 `not-your-video`.
    - Catalog unreachable is a 503 `dependency-unavailable`.
  - **PATCH, end-sale and GET copies** require `SignedIn`, then `IAuthorizationService.AuthorizeAsync(user, edition, new EditionOwnerRequirement())`. A refusal is a 403 `not-your-edition`.
  - **GET edition** stays public.
  - **The client.** Inventory registers `CatalogClient` with `AddServiceClient<CatalogClient>("Services:Catalog")`, as client `inventory`.
- **`InventorySeeder : IDatabaseSeeder<InventoryDbContext>`.** It reads `sample-editions.json`, with entries of the form `{ videoId, creatorId, format, price, copies }`:
  - one Digital edition per sample video, at 4.99 with 1000 copies;
  - a Dvd edition for every fourth video, at 19.99 with 100 copies.

  That is 60 editions. The seeder inserts only when `editions` is empty.
- **The k6 setup in `keycloak.js`:**
  - It gets an admin token (`admin/admin`, from compose.load.yaml's k6 env).
  - It creates client `k6` if it is missing: public, direct access grants only, audience mappers `orders` and `inventory`, and a 30-minute access token lifespan.
  - It exports `passwordToken(username, password)`.

  `last-copy.js` setup signs in as `orbital-kitchen` and creates the test editions on video `…0011` with that token. The overlay adds `Auth__UserClients__1: k6` to inventory.

- [x] **Step 1: Catalog, failing tests, then code**

Tests:
- `Sample_videos_have_fixed_ids`
- `Signed_in_user_opens_a_channel`
- `Second_channel_for_the_same_user_is_409_channel_exists`
- `Taken_slug_is_409`
- `Invalid_slug_is_400`
- `Anonymous_cannot_open_a_channel` (401)
- `Owner_lookup_returns_the_channels_creator`
- `Owner_lookup_needs_the_inventory_client`: a user token is 403, no token is 401.
- `Owner_of_an_unknown_video_is_404`

Implement, then migrate. Expected: Catalog.Tests pass. Commit: `feat(catalog): open a channel, and tell Inventory who owns a video`.

- [x] **Step 2: Inventory, failing tests, then code**

Catalog is a `StubHttpHandler` behind `CatalogClient`. Tests:
- `Create_edition_takes_the_creator_from_the_token`: a `creatorId` in the body is ignored.
- `Create_edition_for_someone_elses_video_is_403`
- `Create_edition_for_an_unknown_video_is_400`
- `Create_edition_when_catalog_is_down_is_503`
- `Creator_cannot_change_another_creators_edition`: PATCH and end-sale are both 403.
- `Creator_can_change_their_own_edition`
- `Copies_list_is_only_for_the_edition_creator`
- `Anyone_can_read_an_edition`
- `Seed_creates_the_sample_editions_once`: run it twice and get 60.

Expected: Inventory.Tests pass. Commit: `feat(inventory): creators manage only their own editions; sample editions`.

- [x] **Step 3: Compose and k6 on the Mac**

1. Wipe the stack and start it: `docker compose down -v && docker compose up -d --wait`.
2. `curl http://localhost:8101/videos/00000000-0000-7000-8001-000000000011/editions` lists a Digital edition. Retry for up to 30 s while the snapshot travels through Kafka.
3. Run the load test, as in Task 3. Expected: green, with p99 under 1 s; record it.
4. CI `compose-smoke` gets the same editions check, with a retry loop.

Commit: `test(load): creators make the test editions with their own token`.

- [x] **Step 4: ADR 0016 and pull request**

1. **ADR 0016, Creator is a resource rule, not a role.**
   - Resource-based authorization, quoting the [ASP.NET Core docs](https://learn.microsoft.com/aspnet/core/security/authorization/resourcebased).
   - Alternatives: a `creator` realm role, which would still need the ownership check; a policy that reads the body.
   - Why `CreatorId` never comes from the request.
2. **Walkthrough.** Interview angles:
   - "Role, claim, policy or resource handler: when each?"
   - "Why 403 and not 404 for someone else's edition?" Give the trade-off.
   - "Where does the ownership check live when two services are involved?"

---

### Task 7: Orders, the checkout saga

Branch `phase-1/orders`. Issue #74. Read: topics 08 (sagas), 10 (async/await) and 13 (resilience). This is the hard part "the checkout saga": the deepest walkthrough of the phase. Advances C-2 (#7) and L-1 (#10).

**Files:**
- **Create:**
  - `src/Services/Orders/`, as in File Structure, with the usual project files, the migrate job, inbox and outbox
  - `tests/Orders.Tests/`
  - `docs/adr/0017-the-checkout-saga.md`
- **Modify, adding lines only:**
  - `NewStore.slnx`
  - `compose.yaml`: `orders`, `orders-migrate`, and the gateway's dependency on orders
  - `init.sql`: the `orders` role and database
  - `ci.yml`: Orders is ready
  - `AGENTS.md`: ports
  - the README layout

**Interfaces:**
- **The model:**

  ```csharp
  public enum OrderStatus { Reserved, Paying, Completed, Expired, Failed, Refunding, Refunded }
  public sealed class Order { Id; BuyerId; EditionId; VideoId; string Format; decimal Price; Guid ReservationId;
      DateTimeOffset ReservedUntil; OrderStatus Status; Guid? PaymentId; string? CheckoutUrl; int? CopyNumber;
      DateTimeOffset CreatedAt; DateTimeOffset UpdatedAt; DateTimeOffset? CompletedAt; uint Version (xmin) }
  // index (buyer_id, edition_id) for the open-order lookup; index (buyer_id, status) for my copies
  ```

- **Clients.** Each is registered with `AddServiceClient`, as client `orders`, using `Services:Inventory` and `Services:Payments`:

  ```csharp
  public sealed class InventoryClient(HttpClient http)
  {
      Task<ReserveResult> ReserveAsync(Guid editionId, Guid reservationId, Guid buyerId, CancellationToken ct);
      Task<HoldResult> HoldAsync(Guid reservationId, CancellationToken ct);
      Task<ConfirmResult> ConfirmAsync(Guid reservationId, CancellationToken ct);
      Task ReleaseAsync(Guid reservationId, CancellationToken ct);
  }
  // ReservationInfo(Guid Id, Guid VideoId, string Format, decimal Price, DateTimeOffset ExpiresAt): Orders' copy of the fields it uses
  // ReserveResult: Reserved(ReservationInfo) | Rejected(int StatusCode, string Type, string Title)   ← 404 and 409 passed through
  // HoldResult:    Held(DateTimeOffset ExpiresAt) | Gone                                             ← 409 expired or released
  // ConfirmResult: Confirmed(int CopyNumber) | Refused                                               ← 409
  // 5xx, timeout or open circuit → TransientFailureException (Task 1)

  public sealed class PaymentsClient(HttpClient http)
  {
      Task<PaymentInfo> CreateAsync(Guid orderId, decimal amount, string returnUrl, CancellationToken ct);  // PaymentInfo(Guid PaymentId, string CheckoutUrl)
  }
  ```

  `returnUrl` is `{Orders:WebBaseUrl}/orders/{id}`. Compose sets `Orders:WebBaseUrl` to `http://localhost:8100`.
- **Endpoints.** They all require `SignedIn`, the buyer is `User.UserId()`, and the audience is `orders`:
  - **`POST /orders { editionId }`:**
    1. If the buyer has an open order for this edition (status `Reserved` or `Paying`, and `ReservedUntil > now`), return it with 200. This is Review Focus 1.
    2. Otherwise generate a reservation id, then reserve.
       - `Reserved` → insert the order as `Reserved`, with price, format and video from the reply. 201 `OrderResponse`.
       - `Rejected` → pass the status and problem type through (sold-out, already-holding, sale-ended, not found), and keep no order.
       - Transient → 503 `dependency-unavailable` with `Retry-After: 5`.

    The resilience handler retries with the same reservation id, so a lost 201 is recovered by the retry.
  - **`POST /orders/{id}/pay`:**
    - Another buyer's order is a 404.
    - `Paying` returns the stored `checkoutUrl`.
    - `Expired` is a 409 `order-expired`.
    - Any other final state is a 409 `order-closed`.
    - `Reserved` goes through these steps:
      1. Hold. `Gone` means the order becomes `Expired`, the reply is 409 `order-expired`, and no payment is made.
      2. `CreateAsync` the payment.
      3. Set `Paying`, `PaymentId`, `CheckoutUrl` and `ReservedUntil` from the hold. 200 `{ checkoutUrl }`.

      A concurrency conflict on the order reloads it and answers from the reloaded state.
  - **`GET /orders/{id}`:** `OrderResponse(Id, EditionId, VideoId, Format, Price, Status, ReservedUntil, CheckoutUrl, CopyNumber)`. Another buyer's order is a 404.
  - **`GET /me/copies`:** the buyer's `Completed` orders, newest first, as `[{ orderId, editionId, videoId, format, copyNumber, price, completedAt, deliveryStatus: null }]`. L-1 has no delivery status until Phase 3.
- **The saga handlers.** Each runs inside the inbox transaction; each call it makes is idempotent.

  | Event | Order status | Action |
  |---|---|---|
  | `PaymentSucceeded` | `Reserved` or `Paying` | Confirm. `Confirmed` → `Completed`, with `CopyNumber`, `CompletedAt` and `PaymentId`. `Refused` → `Refunding`, and enqueue `RefundRequested`. |
  | `PaymentSucceeded` | `Expired` or `Failed` | `Refunding`, and enqueue `RefundRequested`. |
  | `PaymentSucceeded` | `Completed`, `Refunding` or `Refunded` | nothing |
  | `PaymentFailed` | `Reserved` or `Paying` | Release, then `Failed`. |
  | `PaymentRefunded` | `Refunding` | `Refunded`. |

  An unknown order throws `InvalidOperationException`, so the message is dead-lettered. Orders runs no timer: the provider's session expiry is the saga's timeout step.

- [x] **Step 1: The buy and pay endpoints, failing tests**

Inventory and Payments are `StubHttpHandler`s. Tests:
- `Buy_reserves_and_returns_the_order`
- `Buy_when_sold_out_returns_409_and_keeps_no_order`
- `Buy_retries_with_the_same_reservation_id_after_a_lost_reply`: the stub answers 503 once and then 201. Both requests carry one `reservationId`, and there is one order.
- `Buy_again_while_an_order_is_open_returns_that_order` (Review Focus 1): 200, with the same order id, and no second reserve call.
- `Buying_an_edition_you_own_returns_409`: Inventory answers `already-holding`, and no order is kept.
- `Buy_needs_a_signed_in_buyer`: no token is 401, and a service token is 403.
- `Pay_holds_then_creates_the_payment_and_returns_its_url`
- `Double_pay_click_creates_one_payment`: two parallel pays. Every `POST /payments` the stub saw carries the same `orderId`, and both replies carry the same `checkoutUrl`. Payments' unique order id makes it one payment (Task 4).
- `Pay_after_the_reservation_ran_out_marks_the_order_expired`: the hold answers 409. The order is `Expired`, and Payments isn't called.
- `Another_buyers_order_is_404`, for GET and pay.
- `Inventory_down_on_buy_is_503_with_retry_after`

Expected: the tests fail.

- [x] **Step 2: Implement the endpoints, then run the tests**

Commit: `feat(orders): buy and pay, with the reservation and the payment created by Orders`.

- [x] **Step 3: The saga handlers, failing tests, then code**

Tests use Kafka from TestSupport, with events published by `KafkaServer.PublishAsync`:
- `PaymentSucceeded_confirms_and_completes_the_order_with_the_copy_number`
- `Late_PaymentSucceeded_ends_in_a_refund`: confirm answers 409. The order goes to `Refunding`, then `RefundRequested` is in the outbox. Then publish `PaymentRefunded`, and the order is `Refunded`.
- `PaymentFailed_releases_the_reservation_and_fails_the_order`
- `Duplicate_PaymentSucceeded_confirms_once`
- `PaymentSucceeded_waits_while_inventory_is_down_and_completes_later`: confirm answers 503 for 10 s, then 200. The order ends `Completed`, and `payments.payments.dlt` stays empty.
- `My_copies_lists_completed_orders_only`

Expected: they pass after the handlers are written. Commit: `feat(orders): the checkout saga reacts to payment events`.

- [x] **Step 4: Compose, ADR 0017, pull request**

1. **Compose:** `orders` and `orders-migrate`, with `Auth__*` and the caller credentials for client `orders`, and `Services__Inventory` and `Services__Payments`. Add the gateway's dependency on orders; Task 5 already set its `Services__Orders`. Run `docker compose build orders gateway`.
2. **ADR 0017, the checkout saga.**
   - Orchestration with synchronous commands and asynchronous outcomes.
   - Alternatives, each checked and linked: choreography, MassTransit or Temporal sagas, and 2PC.
   - Trade-off: a database transaction stays open during the HTTP call in a handler. Say what bounds it: the resilience timeouts.
3. **Walkthrough, deep: one purchase message by message, and the late payment.** The small change for the owner: make `PaymentFailed` arrive before the hold. Interview angles:
   - "What is a compensating action, and why isn't it a rollback?"
   - "Which calls can you retry, and how do you know?"
   - "What happens if Orders crashes between reserve and saving the order?"

---

### Task 8: The web app

Branch `phase-1/web`. Issue #75. Middle depth (owner's decision 6): short walkthroughs, except for how the app calls the gateway, which is the sign-in path.

**Files:**
- **Create:** `web/`, a new Angular 22.2 workspace (standalone components, plain CSS, no SSR):

  ```
  web/
    proxy.conf.json
    src/app/
      app.routes.ts
      auth/      auth.service.ts  csrf.interceptor.ts  signed-in.guard.ts
      videos/    video-list.component.ts  video-page.component.ts
      orders/    order-page.component.ts
      copies/    my-copies.component.ts
      api/       models.ts
  ```

- **Modify:**
  - `src/Gateway/Dockerfile`: a `node:26.12.1-alpine` stage runs `npm ci` and `npm run build`, and the output goes to `wwwroot`
  - `.dockerignore`: `web/node_modules`, `web/dist` and `web/.angular`
  - `ci.yml`: job `web`
  - `AGENTS.md`, Commands: `npm ci`, `npm test` and `npm start` in `web/`
  - the README layout

**Interfaces:**
- **Routes:**

  | Path | Page |
  |---|---|
  | `''` | the video list, with search |
  | `videos/:id` | the video, its editions and Buy |
  | `orders/:id` | the order |
  | `me/copies` | my copies, behind `signedInGuard` |

- **`AuthService`:**
  - It loads `GET /auth/user` once and keeps the result in a `user` signal.
  - `signIn()` navigates to `/auth/login?returnUrl=<current path>`.
  - `signOut()` navigates to the user's `logoutUrl`.
  - Any API 401 sets the signal to signed out. When the problem type ends in `session-expired`, it also shows a "Your session ended" note.
- **`csrfInterceptor`** adds `X-CSRF: 1` to same-origin requests whose path starts with `/api` or `/auth`, and to nothing else.
- **Pages:**
  - **The list** reads `GET /api/videos?q=&page=`.
  - **The video page** reads `GET /api/videos/{id}` and `/api/videos/{id}/editions`. Each edition shows its format, price and copies left. A signed-out viewer sees "Sign in to buy" instead of Buy.
  - **Buy** posts `/api/orders`, with the button disabled while the request runs, then navigates to `orders/:id`. A 409 shows the problem's title in `buy-error`.
  - **The order page** shows status and `reservedUntil`.
    - `Reserved`: Pay posts `/pay`, then sets `location.href = checkoutUrl`.
    - `Paying`: polls `GET /api/orders/{id}` every 1 s until a final status, for at most 2 minutes, then shows "Still processing, refresh later".
    - `Completed`: shows `copy-number`.
  - **My copies** lists `GET /api/me/copies`. Delivery status shows "—" until Phase 3.
- **`data-testid`s,** which Task 9 relies on:
  - Sign-in: `sign-in`, `user-name`, `sign-out`, `my-copies-link`.
  - Browsing: `search`, `video-card`, `edition` (with `data-format`).
  - Buying: `buy`, `sign-in-to-buy`, `buy-error`.
  - The order: `order-status`, `pay`, `copy-number`, `reserved-until`.
  - My copies: `copy-row`.
- **`proxy.conf.json`** sends `/api`, `/auth`, `/signin-oidc` and `/signout-callback-oidc` to `http://localhost:8100` and keeps the Host header (`changeOrigin: false`). The OIDC redirect then comes back through `ng serve`, which the realm allows on port 4200.

- [x] **Step 1: Create the workspace and check the toolchain**

Run `npx @angular/cli@22.2.6 new web --style=css --ssr=false --skip-git`. Then pin `@angular/*` exactly in `package.json`.

Check that Angular 22.2 supports Node 26.12.1: `npm view @angular/core@22.2.5 engines`. Expected: `^22.22.3 || ^24.15.0 || >=26.0.0`.

Record which test runner `ng new` set up, Vitest or another. Commit: `chore(web): a new Angular workspace`.

- [x] **Step 2: Failing tests, then the auth service and interceptor**

Tests:
- `Adds_the_csrf_header_to_api_and_auth_calls_only`
- `Session_expired_clears_the_user` (Review Focus 3)
- `Signed_in_guard_sends_a_signed_out_viewer_to_sign_in`

Expected: `npm test -- --watch=false` passes. Commit: `feat(web): sign-in state and the CSRF header`.

- [x] **Step 3: Failing tests, then the pages**

Tests:
- `Video_page_shows_sign_in_to_buy_when_signed_out`
- `Buy_disables_the_button_and_opens_the_order`
- `Order_page_polls_until_completed_and_shows_the_copy_number`, with fake timers
- `My_copies_lists_each_copy`

Expected: they pass. Commit: `feat(web): browse, video, order and my copies pages`.

- [x] **Step 4: The image, CI, pull request**

1. Add the gateway Dockerfile stage, then run `docker compose build gateway`. Expected: the image serves the app. Check with `docker run` on a spare port, or in CI.
2. **CI job `web`:**
   - `actions/setup-node`, pinned by SHA as `ci.yml` pins its other actions, with `node-version: 26.12.1` and an npm cache keyed on `web/package-lock.json`;
   - `npm ci`, `npm test -- --watch=false` and `npm run build`.
3. **Walkthrough.** Explain one page in depth: the order page's polling. The rest are short. Interview angles:
   - "Why does the app never see a token, and what does it see instead?"
   - "What is a signal, compared with an observable?"
   - "Why poll here instead of pushing?" Phase 3 brings SignalR.

---

### Task 9: Phase 1b done

Branch `phase-1/done-1b`. Issue #76. Owns the Compose stack. Closes A-1 (#1), C-2 (#7) and L-1 (#10).

**Files:**
- **Create:**
  - `tests/e2e/`: `package.json`, the lock file, `playwright.config.ts`, `checkout.spec.ts`
- **Modify:**
  - `tests/load/last-copy.js`, `tests/load/keycloak.js` and `tests/load/README.md`
  - `compose.load.yaml`
  - `ci.yml`: compose-smoke and the new `e2e` job
  - `AGENTS.md`, Commands: e2e
  - the README: status line and layout

**Interfaces:**
- **Playwright:** `@playwright/test` 1.64.1, Chromium only, `baseURL` `http://localhost:8100`, a trace on the first retry. It runs on the host against the base stack, so `localhost` means what it means to a buyer.
- **`checkout.spec.ts`,** "a new buyer signs up, buys a copy and sees it in my copies":
  1. Open `/` and click `sign-in`. On Keycloak's page, follow Register.
  2. Fill in a random username, an email, a first and last name, and a password, then submit. Expect `user-name` to be visible.
  3. Search `sourdough`, open the video, and click `buy` on the `edition[data-format=Digital]`.
  4. Click `pay`. On FakePay's page, click `fakepay-pay`.
  5. Back on the order page, expect `copy-number` within 30 s.
  6. Open `my-copies-link` and expect one `copy-row` with that number.
- **k6 through the gateway.** `BASE_URL` becomes `http://gateway:8080`. `setupTimeout` is `5m`.
  - **Setup:**
    1. Read the realm's password policy, then set it to `hashAlgorithm(pbkdf2-sha256) and hashIterations(1)`, so 1,020 users can be created and signed in within minutes. Teardown restores the old policy. This is test-only, and the walkthrough says why it is unsafe anywhere else.
    2. Create client `k6`, as in Task 6.
    3. Create 1,020 buyer users with `http.batch`, in batches of 50.
    4. Sign in as the creator and create the editions through the gateway.
    5. Return the editions, the user names and `startAt = now + 20 s`.
  - **Each VU:**
    1. It signs in itself with a password token, tagged `login`, then sleeps until `startAt`.
    2. It posts `POST /api/orders`, tagged `reserve`.
    3. On 201, it posts `/pay` right away.
    4. It pays with `POST http://fakepay:8080/test/sessions/{id}/pay`. The session id is the last segment of `checkoutUrl`.
    5. It polls `GET /api/orders/{id}` every 500 ms for up to 60 s until `Completed`.
  - **The expiry scenario** keeps 1a's checks with its own users and reservations made through `POST /api/orders`.
  - **Teardown,** with the creator token: `GET /api/editions/{id}/copies` holds numbers 1–10, each once, and `copiesLeft` is 0.
  - **Thresholds,** as in 1a, plus:
    - a counter of 5xx responses that must stay at 0;
    - `http_req_duration{name:reserve}` p(99) under `P99_MS`.
  - **The overlay** adds:
    - `Auth__UserClients__1: k6` to gateway, orders and inventory;
    - `FakePay__EnableTestEndpoints: "true"`;
    - the k6 env: Keycloak URL, admin credentials, creator credentials, and the FakePay URL.

    The 3-second `ReservationTtl` gives a winner 3 s between its 201 and pay. If a run shows a winner's hold refused as expired, raise the overlay's TTL to 10 s and record why in `tests/load/README.md`.
- **CI:**
  - `compose-smoke` runs k6 through the gateway on the load overlay, with `P99_MS=2000`.
  - A new job `e2e` starts the base stack, then runs `npm ci` and `npx playwright install --with-deps chromium` in `tests/e2e`, then `npx playwright test`. It uploads the trace as an artifact on failure.
  - The two are separate jobs, because one stack can't have both TTLs.

- [x] **Step 1: The browser test**

Write the spec and run it in CI only (no browser on the Mac, by the owner's decision). Expected: green. Commit: `test(e2e): a new buyer buys a copy in the browser`.

- [x] **Step 2: k6 through the gateway**

Change the scripts and the overlay. Run on the Mac: `docker compose -f compose.yaml -f compose.load.yaml up -d --wait`, then `… run --rm k6`.

Expected: exactly 10 orders `Completed` with numbers 1–10, expired reservations back on sale, no 5xx, burst reserve p99 under `BURST_P99_MS` and steady reserve p99 under 1 s (the rule as the owner changed it on 2026-11-13). If p99 misses, stop and report the numbers (Global Constraints). Record the run in `tests/load/README.md`: p50, p95 and p99 per tag, setup time, and `docker stats` peaks.

Commit: `test(load): the last-copy test buys through the gateway`.

- [x] **Step 3: CI, docs, pull request**

1. The CI jobs from Interfaces.
2. **README:** the status line says 1b is done, waiting for the owner's checks. Add `tests/e2e/` to the layout.
3. **AGENTS.md, Commands:** how to run e2e.
4. **Walkthrough:** what each test proves, and the numbers. `Closes #1`, `Closes #7`, `Closes #10` and `Closes #76`.

---

## Phase 1b is done when

1. **In CI:** on `main`, `build-test`, `web`, `compose-smoke` and `e2e` are green.
   - `compose-smoke` includes k6 through the gateway: exactly 10 orders complete, numbers 1 to 10 are each used once, expired reservations go back on sale, no response is a 5xx, and reserve's p99 is under 3.5 s on the runner (both the burst and the steady scenario).
   - `e2e` is a new buyer signing up, buying and seeing the copy.
2. **On the Mac:** the same k6 run has reserve p99 under 1 s in the steady scenario (and under 3 s in the burst), recorded in `tests/load/README.md`.
3. **On a clean clone:** `docker compose up -d --wait`, then the owner buys once by hand:
   - open http://localhost:8100, sign up, and buy a copy of "Sourdough in Zero Gravity";
   - pay on FakePay's page and see the copy number;
   - see it in "my copies".
4. **Owner:** sprint 3 ends with the five-question mock interview on Phase 1b topics and the Review and Retro sections of `docs/sprints/sprint-03.md`.
