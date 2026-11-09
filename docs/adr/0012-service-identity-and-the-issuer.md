# Service identity: one client per calling service, and one public issuer

## Status

Accepted

## Date

2026-11-09

## Context

From Phase 1b, services call each other (Orders calls Inventory, Inventory calls Catalog). The callee must know who is calling and refuse callers it does not expect. Separately, every token must carry one issuer, although a browser reaches Keycloak at `localhost:8180` and a container reaches it at `keycloak:8080`.

## Decision

- **Client credentials, one Keycloak client per calling service.** `orders` and `inventory` are confidential clients with service accounts only. A caller gets its token with the client credentials grant, which [RFC 6749 section 4.4](https://www.rfc-editor.org/rfc/rfc6749.html#section-4.4) describes as used "when the client is requesting access to the protected resources under its control", and which "MUST only be used by confidential clients". Keycloak's audience mappers put the callee in `aud` (`orders` gets `inventory` and `payments`).
- **The callee names the clients it accepts.** A service validates signature, issuer, audience and lifetime (`AddNewStoreAuth`), then an endpoint requires `Policies.Client("orders")`, which checks `azp`. User clients (`gateway`) have no service accounts and service clients have no user flow, so `azp` alone tells a service's call from a user's.
- **One public issuer.** Compose sets `KC_HOSTNAME=http://localhost:8180` and `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`. The [Keycloak hostname guide](https://www.keycloak.org/server/hostname) says of the second option: "Enables dynamic resolving of backchannel URLs, including hostname, scheme, port and context path.", and "If `hostname-backchannel-dynamic` is set to true, `hostname` must be set to a URL (not just hostname)." (the Validations section). The same guide says: "By explicitly setting the `hostname` option, we avoid a situation where tokens could be issued by a fraudulent issuer." We checked it against Keycloak 26.8.2: a discovery request to `localhost:8180` returns `issuer` on `localhost:8180`, and the same request with `Host: keycloak:8080` returns `issuer` on `localhost:8180` and `token_endpoint` on `keycloak:8080`. A test (`Token_from_the_internal_url_carries_the_public_issuer`) repeats this with a hostname that the test machine cannot resolve. Services set `Auth:Issuer` to the public URL and `Auth:MetadataAddress` to the internal one.

## Alternatives considered

- **Forward the user's token.** Orders would pass the buyer's token to Inventory. Every service that accepts it would then accept every caller holding it with the user's full power. [RFC 9700 section 2.3](https://www.rfc-editor.org/rfc/rfc9700.html#section-2.3) says "access tokens SHOULD be audience-restricted to a specific resource server", and section 4.9.3 recommends audience restriction "to prevent replay of captured access tokens on other resource servers". A forwarded token with several audiences goes against that. Background jobs also have no user token to forward.
- **mutual TLS.** [RFC 8705](https://www.rfc-editor.org/rfc/rfc8705.html) defines client authentication with mutual TLS and certificate-bound tokens. It proves who is calling at the connection level. We did not choose it because it needs a certificate authority and certificate rotation for every service, which is operations work this practice project does not need; client secrets in Compose are enough here. (That cost is our judgement, not a quote.)
- **Trust the Docker network.** Anything that can reach a port is then trusted. This is an anti-goal of the project. [NIST SP 800-207](https://csrc.nist.gov/pubs/sp/800/207/final) puts it this way: "Zero trust assumes there is no implicit trust granted to assets or user accounts based solely on their physical or network location".
- **Token exchange ([RFC 8693](https://www.rfc-editor.org/rfc/rfc8693.html)).** A service trades the user's token for a new one for the next service, "including security tokens employing impersonation and delegation". Keycloak supports it as standard token exchange, for confidential clients only ([Keycloak docs](https://www.keycloak.org/securing-apps/token-exchange)). It fits when a downstream service must act on behalf of the user. Our calls are made by the service itself (reserve a copy, read an edition), so the extra round trip and the delegation rules buy nothing yet. Revisit it when a call needs the user's identity downstream.

## Consequences

- A caller needs `Auth:ClientId`, `Auth:ClientSecret` and `Auth:TokenEndpoint`, and a callee needs one `AddClientPolicy` per accepted caller. Adding a caller to the realm is a config change in two places.
- Client secrets in the realm file equal the client id. They are local-only credentials (AGENTS.md); a real deployment sets them from a secret store.
- A service token is valid until it expires (`expires_in` was 300 seconds from the local Keycloak). `Duende.AccessTokenManagement` caches it and requests a new one shortly before.
- JwtBearer's default clock skew is five minutes, so an expired token still passes for that long. We did not change it.
