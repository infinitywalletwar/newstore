# Load tests

## last-copy.js: 1000 buyers, 10 copies

Proves requirement C-4: the last copy is sold once, however many buyers press the button at the same time.

Run it against the Compose stack (needs Docker; the stack is about 3 GB). The overlay passes the container URLs and the credentials to k6. To run the script yourself against the stack's published ports, the defaults are `BASE_URL=http://localhost:8102`, `TOKEN_URL=http://localhost:8180/realms/newstore/protocol/openid-connect/token` and `KEYCLOAK_URL=http://localhost:8180` (all on the Mac, so they agree). You must also pass the credentials, none of which has a default: `-e ORDERS_CLIENT_SECRET=<secret>` (the `orders` client), `-e CREATOR_PASSWORD=<password>` (the sample creator `orbital-kitchen`, who creates the test editions) and `-e KEYCLOAK_ADMIN_USER=<user> -e KEYCLOAK_ADMIN_PASSWORD=<password>` (`tests/load/keycloak.js` uses the admin API to create the client `k6` when the realm has none). `setup()` fails with a message when one is missing. The stack must be seeded: Inventory asks Catalog who owns the sample video.

```sh
docker compose -f compose.yaml -f compose.load.yaml up -d --wait
docker compose -f compose.yaml -f compose.load.yaml run --rm k6
docker compose -f compose.yaml -f compose.load.yaml down -v
```

To try the other reserve strategy (ADR 0007), set `RESERVE_STRATEGY=ConcurrencyToken` when you run `up`. On a slower machine, raise the latency threshold with, for example, `P99_MS=2000` when you run `run`.

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
| `http_req_duration{name:reserve}: p(99)<P99_MS` | The slowest 1% of reserves took less than `P99_MS` (1000 ms by default; CI uses 3500: the runner is slower and noisier than the Mac, so CI guards against gross regressions only). |
| `http_req_duration{name:confirm}: p(99)<P99_MS` | The same for confirm, which takes a row lock and the edition's number counter. |

`compose.load.yaml` shortens Inventory's reservation TTL to 3 seconds and its sweep interval to 1 second, so the expiry scenario takes seconds, not 10 minutes. It is for this test only; a stack started without it keeps the 10-minute TTL.

## Results

Phase 1b, Task 3: reserve takes a caller-supplied id, looks it up before taking a copy and again when a reserve fails, and every call checks a JWT. ConditionalUpdate, Mac with other work running. "Load" is the 1-minute load average from `uptime` when the run started.

First measurement (commit 59baef1):

| Run | Load | reserve p99 | confirm p99 | Result |
|---|---|---|---|---|
| 1, first run after `up` | 12 to 14 | 1.09 s | not recorded | reserve threshold crossed |
| 2, same stack | 12 to 14 | below 1 s (not recorded) | below 1 s | passed |
| 3, same stack | 12 to 14 | 783 ms | 339 ms | passed |

After the review fix (commit a4ad119, which adds the second id lookup on every failed reserve; the 990 sold-out buyers now do it too):

| Run | Load | reserve p99 | confirm p99 | Result |
|---|---|---|---|---|
| 1 | 9.9 | 771 ms | 361 ms | passed |
| 2 | 8.3 | 716 ms | 351 ms | passed |
| 3 | 8.8 | 1.06 s | 448 ms | reserve threshold crossed |

Every run sold exactly 10 copies, numbered 1 to 10 in both editions, with 990 `sold-out`, no 5xx and all 59 checks passing. The threshold was not changed. Reserve p99 for 1000 simultaneous buyers sits between about 0.7 s and 1.1 s on this Mac, so a single run near 1 s is noise or a real regression only when it repeats.

#97: ConditionalUpdate reserves in one statement and looks the id up only when that statement took no copy (ADR 0013, "Amendment, 2026-11"). One session on the Mac, 2026-11-13, other work running; the stack now includes Payments and FakePay. Every run is a fresh `down -v` and `up -d --wait`, then 60 seconds before k6 starts (except base run 1), with `P99_MS=5000` so every run finishes. "Load" is the 1-minute load average from `uptime` just before k6 started. Runs are in the order they ran. "Before Task 3" is Inventory at c63e6c0 (no id lookups, no JWT check) in the same stack and with the same k6 script.

| Run | Inventory | Load | reserve p99 | reserve median | confirm p99 |
|---|---|---|---|---|---|
| base 1, no 60 s pause | Task 3 (a77c6d1) | 11.1 | 1.26 s | 988 ms | 647 ms |
| base 2 | Task 3 | 6.7 | 1.12 s | 900 ms | 602 ms |
| base 3 | Task 3 | 7.6 | 1.03 s | 736 ms | 596 ms |
| new 1 | this change (d956d3a) | 4.1 | 727 ms | 566 ms | 427 ms |
| new 2 | this change | 5.5 | 436 ms | 376 ms | 286 ms |
| pre 1 | before Task 3 (c63e6c0) | 6.9 | 586 ms | 477 ms | 465 ms |
| new 3 | this change | 8.8 | 663 ms | 580 ms | 360 ms |
| pre 2 | before Task 3 | 8.2 | 657 ms | 502 ms | 407 ms |
| base 4 | Task 3 | 7.1 | 992 ms | 834 ms | 402 ms |
| pre 3 | before Task 3 | 6.0 | 295 ms | 218 ms | 259 ms |

Every run sold exactly 10 copies with 990 `sold-out`, no failed request and every check passing. Reserve p99 with this change, 0.44 to 0.73 s, is back in the range of the code before Task 3, 0.30 to 0.66 s, and below Task 3, 0.99 to 1.26 s; the medians show the same (376 to 580 ms, 218 to 502 ms, 736 to 988 ms). A run varies by a few hundred milliseconds on this Mac (before Task 3: 295 ms and 657 ms), so compare ranges, not single runs. CI is back at `P99_MS=2000`.

CI numbers for the same code (head 49cba7a, run 43358510750, three attempts, `P99_MS` 2000 at the time): reserve p99 1.95 s (pass), 1.62 s (pass), 2.75 s (fail at 2000). The runner is slower and noisier than the Mac, so the numbers above and these are not comparable one to one.
