# Load tests

## last-copy.js: 1000 buyers, 10 copies

Proves requirement C-4: the last copy is sold once, however many buyers press the button at the same time.

Run it against the Compose stack (needs Docker; the stack is about 3 GB):

```sh
docker compose -f compose.yaml -f compose.load.yaml up -d --wait
docker compose -f compose.yaml -f compose.load.yaml run --rm k6
docker compose -f compose.yaml -f compose.load.yaml down -v
```

To try the other reserve strategy (ADR 0007), set `RESERVE_STRATEGY=ConcurrencyToken` when you run `up`. On a slower machine, raise the latency threshold with `P99_MS=2000` when you run `run`.

What it does:

- `setup()` creates two editions of 10 copies each and gets a client-credentials token for the client `orders` from Keycloak (`http://keycloak:8080/realms/newstore/protocol/openid-connect/token`). The secret comes from `compose.load.yaml` (`ORDERS_CLIENT_SECRET: orders`). Only `orders` may reserve and confirm (ADR 0012, 0013), so every reserve and confirm sends that token, and every reserve body carries a new `reservationId`.
- Scenario `last_copy`: 1000 virtual users, one iteration each, each a different buyer. A user reserves the first edition. The 10 who get a `201` confirm twice and check that both replies carry the same copy number. The other 990 must get `409 sold-out`.
- Scenario `expiry`: 10 users reserve the second edition and walk away. After 6 seconds the reservations have expired (TTL 3 s plus a 1 s sweep), the copies are back, and 10 new buyers reserve and confirm them.
- `teardown()` reads both editions back: `copiesLeft` is 0 and the copy numbers are exactly 1 to 10, with no gap and no duplicate.

The thresholds, and what failing each one means:

| Threshold | Meaning |
|---|---|
| `checks: rate==1` | Every check passed: no wrong status, no different number on the second confirm, numbers 1..10. |
| `copies_confirmed: count==10` | Exactly 10 buyers won. More is overselling, fewer is a lost copy. |
| `sold_out: count==990` | Every other buyer got a clean `409 sold-out`. |
| `http_req_failed: rate==0` | No 5xx and no connection error. A `409` counts as a good answer here. |
| `http_req_duration{name:reserve}: p(99)<P99_MS` | The slowest 1% of reserves took less than `P99_MS` (1000 ms by default; CI uses 2000). |
| `http_req_duration{name:confirm}: p(99)<P99_MS` | The same for confirm, which takes a row lock and the edition's number counter. |

`compose.load.yaml` shortens Inventory's reservation TTL to 3 seconds and its sweep interval to 1 second, so the expiry scenario takes seconds, not 10 minutes. It is for this test only; a stack started without it keeps the 10-minute TTL.

## Results

Phase 1b, Task 3 (reserve takes a caller-supplied id, a lookup before each reserve, and every call checks a JWT), ConditionalUpdate, the Mac with other builds running at the same time (load average 12 to 14):

| Run | reserve p99 | confirm p99 | Result |
|---|---|---|---|
| 1, first run after `up` | 1.09 s | not recorded | reserve threshold crossed |
| 2, same stack | below 1 s (not recorded) | below 1 s | all thresholds passed |
| 3, same stack | 783 ms | 339 ms | all thresholds passed |

Every run sold exactly 10 copies, numbered 1 to 10 in both editions, with 990 `sold-out` and no 5xx. Run 1 was the first one against a freshly started stack while the machine was busy; the threshold was not changed.
