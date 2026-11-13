// Two jobs through the gateway (tests/load/README.md):
//  - the burst (scenarios last_copy and expiry): 1000 buyers fight for the last 10 copies at once, and the winners pay. It is the
//    correctness test: exactly 10 orders complete with distinct copy numbers 1..10, expired holds go back on sale, nobody gets a
//    5xx. Its latency bound is soft (BURST_P99_MS).
//  - the steady load (scenario steady): buyers arrive at a rate that ramps up and holds, nobody is refused. It is the latency
//    rule: reserve p99 under P99_MS (1 s on the Mac).
import http from 'k6/http';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

import { BUYER_PASSWORD, createUsers, ensureK6Client, getPasswordPolicy, passwordToken, setPasswordPolicy } from './keycloak.js';

// Compose passes the container URLs (compose.load.yaml); the defaults are the Mac's published ports.
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8100';
const FAKEPAY_URL = __ENV.FAKEPAY_URL || 'http://localhost:8105';
const P99_MS = __ENV.P99_MS || '1000';
const BURST_P99_MS = __ENV.BURST_P99_MS || '3000';
const COPIES = 10;
const BUYERS = 1000;
// 1,000 buyers, plus ten holders and ten new buyers in the expiry scenario.
const USER_COUNT = BUYERS + 2 * COPIES;
// Editions are a creator's to make (C-1): the script signs in as the sample creator who owns this sample video, "Sourdough in Zero
// Gravity". Inventory asks Catalog who owns it, so the stack must be seeded.
const VIDEO_ID = '00000000-0000-7000-8001-000000000011';
const CREATOR = 'orbital-kitchen';
// No default: compose.load.yaml passes it, and setup() fails with a message when it is missing.
const CREATOR_PASSWORD = __ENV.CREATOR_PASSWORD;
const JSON_HEADERS = { 'Content-Type': 'application/json' };
// Test-only: with one hash iteration 1,020 users can be created and signed in within minutes. Unsafe anywhere else (README).
const WEAK_POLICY = 'hashAlgorithm(pbkdf2-sha256) and hashIterations(1)';
// Every buyer signs in on its own and then waits for this moment, so the reserves are as simultaneous as the VUs allow.
const START_DELAY_MS = 20_000;
// The steady scenario: 300 buyers a second is about a million buys an hour, far above what Phase 1 expects, and it is a rate this
// stack serves (the 1,000 buyers of the burst arrive within 0.3 s, a rate of 3,000 a second, which no single Mac VM serves under
// 1 s: tests/load/README.md). It ramps for 15 s so the processes and pools grow into it, then holds for 10 s.
const STEADY_RATE = 300;
// Buyers sign in once per VU and buy from many editions in turn, so a buyer is not handed its own open order back (a 200, not a
// reservation) before the 3 s hold has expired: 100 editions keep a VU's repeat on the same edition 5 s or more apart.
const STEADY_EDITIONS = 100;
const STEADY_START = '40s';

const copiesConfirmed = new Counter('copies_confirmed');
const soldOut = new Counter('sold_out');
const serverErrors = new Counter('server_errors');
// How long after `startAt` a VU was ready (0 or less: it waited for the others). A positive p95 means the burst was spread out.
const lateStart = new Trend('late_start_ms');

// VU-local state of the steady scenario: each VU signs in once.
let steadyToken = null;
let steadyTurn = 0;

// A 409 is an answer the test expects, not a failed request.
http.setResponseCallback(http.expectedStatuses(200, 201, 204, 409));

export const options = {
  setupTimeout: '5m',
  scenarios: {
    last_copy: {
      executor: 'per-vu-iterations',
      exec: 'lastCopy',
      vus: BUYERS,
      iterations: 1,
      maxDuration: '3m',
    },
    // Ten buyers hold the second edition's ten copies and never pay. The overlay's 3 s TTL and 1 s sweep give the copies
    // back, and ten new buyers get them and pay.
    expiry: {
      executor: 'per-vu-iterations',
      exec: 'expiry',
      vus: COPIES,
      iterations: 1,
      maxDuration: '3m',
    },
    // Starts after the burst has finished (it ends about 30 s in).
    steady: {
      executor: 'ramping-arrival-rate',
      exec: 'steady',
      startTime: STEADY_START,
      startRate: 10,
      timeUnit: '1s',
      preAllocatedVUs: 150,
      maxVUs: 600,
      stages: [
        { target: STEADY_RATE, duration: '15s' },
        { target: STEADY_RATE, duration: '10s' },
      ],
    },
  },
  thresholds: {
    checks: ['rate==1'],
    copies_confirmed: ['count==10'],
    sold_out: ['count==990'],
    http_req_failed: ['rate==0'],
    server_errors: ['count==0'],
    // The burst's bound is soft: 1,000 buyers in 0.3 s queue at every hop (tests/load/README.md). The rule is the steady load's.
    'http_req_duration{name:reserve}': ['p(99)<' + BURST_P99_MS],
    'http_req_duration{name:reserve-steady}': ['p(99)<' + P99_MS],
    // k6 could not start every arrival it was asked for: the stack (or k6) fell behind the rate.
    dropped_iterations: ['count==0'],
    // Not limits: a threshold on a tag makes k6 print that tag's own line in the summary, which the README's table reads.
    'http_req_duration{name:login}': ['p(99)<60000'],
    'http_req_duration{name:pay}': ['p(99)<60000'],
    'http_req_duration{name:fakepay-pay}': ['p(99)<60000'],
    'http_req_duration{name:poll}': ['p(99)<60000'],
  },
};

function userName(i) {
  return `k6-buyer-${i}`;
}

function call(method, path, body, name, token) {
  const headers = token ? { ...JSON_HEADERS, Authorization: `Bearer ${token}` } : JSON_HEADERS;
  const res = http.request(method, `${BASE_URL}${path}`, body === null ? null : JSON.stringify(body), { headers, tags: { name } });
  if (res.status >= 500) {
    serverErrors.add(1);
  }
  return res;
}

function createEdition(creatorToken, copies = COPIES) {
  const res = call('POST', '/api/editions', { videoId: VIDEO_ID, format: 'Digital', price: 9.99, copies }, 'create-edition', creatorToken);
  check(res, { 'edition created': (r) => r.status === 201 });
  return res.json('id');
}

export function setup() {
  if (!CREATOR_PASSWORD) {
    throw new Error('CREATOR_PASSWORD is not set: pass the sample creator\'s password, for example -e CREATOR_PASSWORD=<password>.');
  }
  const started = Date.now();
  const oldPolicy = getPasswordPolicy();
  setPasswordPolicy(WEAK_POLICY);
  try {
    ensureK6Client();
    const users = Array.from({ length: USER_COUNT }, (_, i) => userName(i));
    createUsers(users, 50);
    const creatorToken = passwordToken(CREATOR, CREATOR_PASSWORD);
    const first = createEdition(creatorToken);
    const second = createEdition(creatorToken);
    const steady = Array.from({ length: STEADY_EDITIONS }, () => createEdition(creatorToken, 1000));
    console.log(`setup took ${((Date.now() - started) / 1000).toFixed(1)} s`);
    return { first, second, steady, users, creatorToken, oldPolicy, startAt: Date.now() + START_DELAY_MS };
  } catch (e) {
    // teardown() does not run when setup() throws, so put the policy back here.
    setPasswordPolicy(oldPolicy);
    throw e;
  }
}

function waitForStart(startAt) {
  const wait = startAt - Date.now();
  lateStart.add(-wait);
  if (wait > 0) {
    sleep(wait / 1000);
  }
}

// Pay can answer 409 order-busy or 503: both are retryable. Anything else is final.
function pay(orderId, token) {
  let res;
  for (let attempt = 1; attempt <= 3; attempt++) {
    res = call('POST', `/api/orders/${orderId}/pay`, null, 'pay', token);
    const retryable = res.status === 503 || (res.status === 409 && res.json('type') === 'urn:newstore:problem:order-busy');
    if (!retryable) {
      break;
    }
    sleep(0.1);
  }
  return res;
}

// A winner's way: pay at FakePay's test endpoint, then poll until Completed. Returns the copy number, or null.
function payAndWait(orderId, token) {
  const paid = pay(orderId, token);
  check(paid, { 'pay is 200': (r) => r.status === 200 });
  if (paid.status !== 200) {
    return null;
  }
  const sessionId = paid.json('checkoutUrl').split('/').pop();
  const card = http.post(`${FAKEPAY_URL}/test/sessions/${sessionId}/pay`, null, { tags: { name: 'fakepay-pay' } });
  check(card, { 'fakepay pay is 204': (r) => r.status === 204 });

  // Every 500 ms for up to 60 s.
  for (let i = 0; i < 120; i++) {
    const order = call('GET', `/api/orders/${orderId}`, null, 'poll', token);
    if (order.status === 200 && order.json('status') === 'Completed') {
      return order.json('copyNumber');
    }
    sleep(0.5);
  }
  check(null, { 'order completed within 60 s': () => false });
  return null;
}

function buy(editionId, token) {
  return call('POST', '/api/orders', { editionId }, 'reserve', token);
}

export function lastCopy(data) {
  const token = passwordToken(data.users[exec.scenario.iterationInTest], BUYER_PASSWORD);
  waitForStart(data.startAt);
  const order = buy(data.first, token);

  if (order.status === 201) {
    const number = payAndWait(order.json('id'), token);
    check(null, { 'a winner got a copy number': () => number !== null && number >= 1 });
    copiesConfirmed.add(1);
  } else if (order.status === 409 && order.json('type') === 'urn:newstore:problem:sold-out') {
    soldOut.add(1);
  } else {
    check(order, { 'order is 201 or 409 sold-out': () => false });
  }
}

export function expiry(data) {
  const i = exec.scenario.iterationInTest;
  const holderToken = passwordToken(data.users[BUYERS + i], BUYER_PASSWORD);
  const buyerToken = passwordToken(data.users[BUYERS + COPIES + i], BUYER_PASSWORD);
  waitForStart(data.startAt);

  // The holder never pays. Its order stays Reserved with a past reservedUntil until a Pay heals it (ADR 0017), so the proof
  // that the copy is back on sale is the new buyer's 201 and the numbers 1..10 in teardown, not the old order's status.
  const held = buy(data.second, holderToken);
  check(held, { 'expiry: the hold is 201': (r) => r.status === 201 });

  // TTL 3 s + sweep 1 s + margin.
  sleep(6);

  const again = buy(data.second, buyerToken);
  check(again, { 'expiry: a new buyer after expiry gets 201': (r) => r.status === 201 });
  if (again.status === 201) {
    const number = payAndWait(again.json('id'), buyerToken);
    check(null, { 'expiry: the new buyer got a copy number': () => number !== null && number >= 1 });
  }
}

// One arrival of the steady load. Nobody is refused: the editions have far more copies than the holds that exist at once.
export function steady(data) {
  if (steadyToken === null) {
    steadyToken = passwordToken(data.users[exec.vu.idInTest % BUYERS], BUYER_PASSWORD);
  }
  const edition = data.steady[(exec.vu.idInTest + steadyTurn++) % STEADY_EDITIONS];
  const order = call('POST', '/api/orders', { editionId: edition }, 'reserve-steady', steadyToken);
  check(order, { 'steady: the buy is 201': (r) => r.status === 201 });
}

function numbersAreOneToTen(editionId, label, creatorToken) {
  // Every route through the gateway needs a token, the edition read too.
  const headers = { Authorization: `Bearer ${creatorToken}` };
  const edition = http.get(`${BASE_URL}/api/editions/${editionId}`, { headers, tags: { name: 'check-edition' } }).json();
  const copies = http.get(`${BASE_URL}/api/editions/${editionId}/copies`, { headers, tags: { name: 'check-copies' } }).json();
  const numbers = copies.map((c) => c.number).sort((a, b) => a - b);
  check(null, {
    [`${label}: copiesLeft is 0`]: () => edition.copiesLeft === 0,
    [`${label}: copiesSold is ${COPIES}`]: () => edition.copiesSold === COPIES,
    [`${label}: copy numbers are exactly 1..${COPIES}`]: () =>
      numbers.length === COPIES && numbers.every((n, i) => n === i + 1),
  });
}

export function teardown(data) {
  numbersAreOneToTen(data.first, 'first edition', data.creatorToken);
  numbersAreOneToTen(data.second, 'second edition', data.creatorToken);
  setPasswordPolicy(data.oldPolicy);
}
