// Keycloak for the load test: makes sure the public client `k6` exists, and signs a user in through it.
// The client is a stand-in for the browser: a user client (Inventory accepts it once `Auth__UserClients__1: k6` is set in
// compose.load.yaml) that needs no secret and no redirect. Imported by last-copy.js.
import http from 'k6/http';
import { check } from 'k6';

// The Mac's published port; Compose passes the container URL (compose.load.yaml).
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://localhost:8180';
// Local-only credentials (AGENTS.md). Compose passes them; there is no default, as with the orders secret.
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

function adminToken() {
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

// A user's access token, as the browser would get through the gateway.
export function passwordToken(username, password) {
  const res = http.post(TOKEN_URL, {
    grant_type: 'password',
    client_id: CLIENT_ID,
    username,
    password,
  }, { tags: { name: 'keycloak-user-token' } });
  check(res, { [`${username} signed in`]: (r) => r.status === 200 });
  return res.json('access_token');
}
