// Keycloak for the load test: makes sure the public client `k6` exists, creates the buyers, and signs a user in through it.
// The client is a stand-in for the browser: a user client (the gateway, Orders and Inventory accept it once
// `Auth__UserClients__1: k6` is set in compose.load.yaml) that needs no secret and no redirect. Imported by last-copy.js.
import http from 'k6/http';
import { check } from 'k6';

// The Mac's published port; Compose passes the container URL (compose.load.yaml).
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://localhost:8180';
// Local-only credentials (AGENTS.md). Compose passes them; there is no default.
const ADMIN_USER = __ENV.KEYCLOAK_ADMIN_USER;
const ADMIN_PASSWORD = __ENV.KEYCLOAK_ADMIN_PASSWORD;

const REALM = 'newstore';
const CLIENT_ID = 'k6';
const TOKEN_URL = `${KEYCLOAK_URL}/realms/${REALM}/protocol/openid-connect/token`;
// The whole run is about a minute, but the setup token must outlive a slow start: 30 minutes, not the realm's 5.
const ACCESS_TOKEN_LIFESPAN_SECONDS = '1800';

function audienceMapper(audience) {
  return {
    name: `audience-${audience}`,
    protocol: 'openid-connect',
    protocolMapper: 'oidc-audience-mapper',
    consentRequired: false,
    config: {
      'included.custom.audience': audience,
      'id.token.claim': 'false',
      'access.token.claim': 'true',
      'introspection.token.claim': 'true',
    },
  };
}

// A fresh admin token for each step, so a long job never depends on one token's lifetime.
export function adminToken() {
  if (!ADMIN_USER || !ADMIN_PASSWORD) {
    throw new Error('KEYCLOAK_ADMIN_USER and KEYCLOAK_ADMIN_PASSWORD are not set: pass them with -e.');
  }
  const res = http.post(`${KEYCLOAK_URL}/realms/master/protocol/openid-connect/token`, {
    grant_type: 'password',
    client_id: 'admin-cli',
    username: ADMIN_USER,
    password: ADMIN_PASSWORD,
  }, { tags: { name: 'keycloak-admin-token' } });
  check(res, { 'admin token issued': (r) => r.status === 200 });
  return res.json('access_token');
}

// Creates the client `k6` when the realm has none: public, direct access grants only, audiences `orders` and `inventory`.
export function ensureK6Client() {
  const headers = { Authorization: `Bearer ${adminToken()}`, 'Content-Type': 'application/json' };
  const clients = `${KEYCLOAK_URL}/admin/realms/${REALM}/clients`;
  const found = http.get(`${clients}?clientId=${CLIENT_ID}`, { headers, tags: { name: 'keycloak-find-client' } });
  check(found, { 'client lookup succeeded': (r) => r.status === 200 });
  if (found.json().length > 0) {
    return;
  }

  const created = http.post(clients, JSON.stringify({
    clientId: CLIENT_ID,
    protocol: 'openid-connect',
    enabled: true,
    publicClient: true,
    directAccessGrantsEnabled: true,
    standardFlowEnabled: false,
    implicitFlowEnabled: false,
    serviceAccountsEnabled: false,
    attributes: { 'access.token.lifespan': ACCESS_TOKEN_LIFESPAN_SECONDS },
    protocolMappers: [audienceMapper('orders'), audienceMapper('inventory')],
  }), { headers, tags: { name: 'keycloak-create-client' } });
  check(created, { 'client k6 created': (r) => r.status === 201 });
}

export const BUYER_PASSWORD = 'k6-buyer-password';
const USERS = `${KEYCLOAK_URL}/admin/realms/${REALM}/users`;

function adminHeaders() {
  return { Authorization: `Bearer ${adminToken()}`, 'Content-Type': 'application/json' };
}

// The realm's password policy ('' when it has none). Read before setup() weakens it, so teardown() can put it back.
export function getPasswordPolicy() {
  const res = http.get(`${KEYCLOAK_URL}/admin/realms/${REALM}`, { headers: adminHeaders(), tags: { name: 'keycloak-get-realm' } });
  check(res, { 'realm read': (r) => r.status === 200 });
  return res.json('passwordPolicy') || '';
}

// Sends only `passwordPolicy` (Keycloak answered 204 in every run, and the old policy, empty, was restored).
export function setPasswordPolicy(policy) {
  const res = http.put(`${KEYCLOAK_URL}/admin/realms/${REALM}`, JSON.stringify({ passwordPolicy: policy }),
    { headers: adminHeaders(), tags: { name: 'keycloak-set-policy' } });
  check(res, { 'password policy set': (r) => r.status === 204 });
}

// Creates the buyers `names` in batches of `size` parallel requests. A user that already exists (409, a second run on the
// same stack) is fine.
export function createUsers(names, size) {
  for (let i = 0; i < names.length; i += size) {
    const headers = adminHeaders();
    const batch = names.slice(i, i + size).map((name) => ['POST', USERS, JSON.stringify({
      username: name,
      email: `${name}@example.test`,
      firstName: 'K6',
      lastName: 'Buyer',
      enabled: true,
      emailVerified: true,
      credentials: [{ type: 'password', value: BUYER_PASSWORD, temporary: false }],
    }), { headers, tags: { name: 'keycloak-create-user' } }]);
    for (const res of http.batch(batch)) {
      check(res, { 'buyer created': (r) => r.status === 201 || r.status === 409 });
    }
  }
}

// A user's access token, as the browser would get through the gateway. The request is tagged `login`.
export function passwordToken(username, password) {
  const res = http.post(TOKEN_URL, {
    grant_type: 'password',
    client_id: CLIENT_ID,
    username,
    password,
  }, { tags: { name: 'login' } });
  check(res, { 'signed in': (r) => r.status === 200 });
  return res.json('access_token');
}
