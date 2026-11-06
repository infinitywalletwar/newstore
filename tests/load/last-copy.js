// 1000 buyers fight for the last copies (C-4). Passes only if exactly 10 reservations win, each gets a
// distinct copy number 1..10, and nobody gets a 5xx. See tests/load/README.md.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8102';
const P99_MS = __ENV.P99_MS || '1000';
const COPIES = 10;
const JSON_HEADERS = { 'Content-Type': 'application/json' };

// Version-4 UUID shape; Math.random is fine for test IDs, and the script needs no network import.
function uuidv4() {
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

const copiesConfirmed = new Counter('copies_confirmed');
const soldOut = new Counter('sold_out');

// A 409 is an answer the test expects, not a failed request.
http.setResponseCallback(http.expectedStatuses(200, 201, 204, 409));

export const options = {
  scenarios: {
    last_copy: {
      executor: 'per-vu-iterations',
      exec: 'lastCopy',
      vus: 1000,
      iterations: 1,
      maxDuration: '2m',
    },
    // Ten buyers hold the second edition's ten copies and walk away. The overlay's 3 s TTL and 1 s
    // sweep give the copies back, and ten new buyers get them.
    expiry: {
      executor: 'per-vu-iterations',
      exec: 'expiry',
      vus: 10,
      iterations: 1,
      maxDuration: '2m',
    },
  },
  thresholds: {
    checks: ['rate==1'],
    copies_confirmed: ['count==10'],
    sold_out: ['count==990'],
    http_req_failed: ['rate==0'],
    'http_req_duration{name:reserve}': ['p(99)<' + P99_MS],
  },
};

function post(path, body, name) {
  return http.post(`${BASE_URL}${path}`, body === null ? null : JSON.stringify(body), {
    headers: JSON_HEADERS,
    tags: { name },
  });
}

function createEdition() {
  const res = post('/editions', {
    videoId: uuidv4(),
    creatorId: uuidv4(),
    format: 'Digital',
    price: 9.99,
    copies: COPIES,
  }, 'create-edition');
  check(res, { 'edition created': (r) => r.status === 201 });
  return res.json('id');
}

export function setup() {
  return { first: createEdition(), second: createEdition() };
}

export function lastCopy(data) {
  const reserve = post(`/editions/${data.first}/reservations`, { buyerId: uuidv4() }, 'reserve');

  if (reserve.status === 201) {
    const id = reserve.json('id');
    // Confirming twice must give the same number: confirm is idempotent.
    const one = post(`/reservations/${id}/confirm`, null, 'confirm');
    const two = post(`/reservations/${id}/confirm`, null, 'confirm');
    check(null, {
      'both confirms are 200': () => one.status === 200 && two.status === 200,
      'both confirms carry the same copy number': () =>
        one.json('copyNumber') !== null && one.json('copyNumber') === two.json('copyNumber'),
    });
    copiesConfirmed.add(1);
  } else if (reserve.status === 409 && reserve.json('type') === 'urn:newstore:problem:sold-out') {
    soldOut.add(1);
  } else {
    check(reserve, { 'reserve is 201 or 409 sold-out': () => false });
  }
}

export function expiry(data) {
  const first = post(`/editions/${data.second}/reservations`, { buyerId: uuidv4() }, 'reserve');
  check(first, { 'expiry: first reserve is 201': (r) => r.status === 201 });

  // TTL 3 s + sweep 1 s + margin.
  sleep(6);

  const again = post(`/editions/${data.second}/reservations`, { buyerId: uuidv4() }, 'reserve');
  check(again, { 'expiry: reserve after expiry is 201': (r) => r.status === 201 });
  if (again.status === 201) {
    const confirm = post(`/reservations/${again.json('id')}/confirm`, null, 'confirm');
    check(confirm, {
      'expiry: confirm returns a number': (r) => r.status === 200 && r.json('copyNumber') >= 1,
    });
  }
}

function numbersAreOneToTen(editionId, label) {
  const edition = http.get(`${BASE_URL}/editions/${editionId}`).json();
  const copies = http.get(`${BASE_URL}/editions/${editionId}/copies`).json();
  const numbers = copies.map((c) => c.number).sort((a, b) => a - b);
  check(null, {
    [`${label}: copiesLeft is 0`]: () => edition.copiesLeft === 0,
    [`${label}: copiesSold is ${COPIES}`]: () => edition.copiesSold === COPIES,
    [`${label}: copy numbers are exactly 1..${COPIES}`]: () =>
      numbers.length === COPIES && numbers.every((n, i) => n === i + 1),
  });
}

export function teardown(data) {
  numbersAreOneToTen(data.first, 'first edition');
  numbersAreOneToTen(data.second, 'second edition');
}
