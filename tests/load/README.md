# Load tests

## last-copy.js: 1000 buyers, 10 copies, through the gateway

Two jobs, in one run. The **burst** (scenarios `last_copy` and `expiry`) proves requirement C-4: the last copy is sold once, however many buyers press the button at the same time, and that a buyer who wins pays and gets the copy through the whole checkout (gateway, Orders, Inventory, Payments, FakePay). The **steady load** (scenario `steady`) holds the latency rule: reserve p99 under 1 s on the Mac when buyers arrive at a rate the stack serves. The owner split the two on 2026-11-13, after a diagnosis showed that the p99 of 1,010 requests arriving within 0.3 s is the time the stack needs to drain them, and that it moved between 1.3 and 3.0 s with the Mac's load (see "The rule" below).

Run it against the Compose stack (needs Docker; the stack is about 3 GB). The overlay passes the container URLs and the credentials to k6. To run the script yourself against the stack's published ports, the defaults are `BASE_URL=http://localhost:8100` (the gateway), `FAKEPAY_URL=http://localhost:8105` and `KEYCLOAK_URL=http://localhost:8180` (all on the Mac, so they agree). You must also pass the credentials, none of which has a default: `-e CREATOR_PASSWORD=<password>` (the sample creator `orbital-kitchen`, who creates the test editions) and `-e KEYCLOAK_ADMIN_USER=<user> -e KEYCLOAK_ADMIN_PASSWORD=<password>` (`tests/load/keycloak.js` uses the admin API to create the client `k6`, set the password policy and create the buyers). `setup()` fails with a message when one is missing. The stack must be seeded: Inventory asks Catalog who owns the sample video.

```sh
docker compose -f compose.yaml -f compose.load.yaml up -d --wait
docker compose -f compose.yaml -f compose.load.yaml run --rm k6
docker compose -f compose.yaml -f compose.load.yaml down -v
```

To try the other reserve strategy (ADR 0007), set `RESERVE_STRATEGY=ConcurrencyToken` when you run `up`. `P99_MS` (default 1000) is the steady load's bound and `BURST_P99_MS` (default 3000) the burst's; CI sets both to 3500.

What it does:

- `setup()` (takes about 4 s on the Mac, printed as `setup took`):
  1. Reads the realm's password policy and sets it to `hashAlgorithm(pbkdf2-sha256) and hashIterations(1)`. **This is test-only and unsafe anywhere else:** a password hash with one iteration can be guessed offline in no time if the hashes leak. It is here so that 1,020 users can be created and signed in within seconds. `teardown()` puts the old policy back (the realm has none, so it sets an empty one), and `setup()` does the same if it fails, because k6 does not run `teardown()` when `setup()` throws.
  2. Creates the public client `k6` (password grant, tokens that last 30 minutes), as a stand-in for the browser. The gateway, Orders and Inventory accept it as a second user client (`Auth__UserClients__1: k6` in `compose.load.yaml`).
  3. Creates 1,020 buyers `k6-buyer-0` to `k6-buyer-1019`, in batches of 50 requests (`http.batch`). A buyer that already exists (a second run on the same stack) is fine.
  4. Signs in as the creator and creates two editions of 10 copies each through the gateway (`POST /api/editions`).
  5. Creates 100 more editions of 1,000 copies each for the steady load.
  6. Returns the editions, the buyers and `startAt = now + 20 s`.
- Scenario `last_copy`: 1000 virtual users, one iteration each, each a different buyer. A VU signs in with a password token (tagged `login`), waits for `startAt`, then posts `POST /api/orders` (tagged `reserve`). The 10 who get a `201` pay: `POST /api/orders/{id}/pay` (tagged `pay`; a 409 `order-busy` or a 503 is retried up to 3 times after 100 ms), then `POST /test/sessions/{id}/pay` at FakePay (the session id is the last segment of `checkoutUrl`), then poll `GET /api/orders/{id}` every 500 ms for up to 60 s until `Completed`. The other 990 must get `409 sold-out`.
- Scenario `expiry`: 10 holders post `POST /api/orders` for the second edition and never pay. After 6 seconds (TTL 3 s plus a 1 s sweep) 10 new buyers post `POST /api/orders`, must get `201` and pay like winners. An order that is never paid stays `Reserved` with a past `reservedUntil` until a later Pay heals it (ADR 0017), so the proof that the copies are back on sale is the new buyers' `201`s and the numbers 1 to 10 in `teardown()`, not the holders' order status.
- Scenario `steady` (starts 40 s in, after the burst has finished): a `ramping-arrival-rate` executor, 10 to 300 buys a second over 15 s, then 300 a second for 10 s, about 6,300 buys. 300 a second is about a million buys an hour, far above what Phase 1 expects; the stack's measured steady p99 at that rate is well under 1 s (table below), so the rate is a load the stack carries, not a limit it was tuned to. Each VU signs in once and buys from the 100 editions in turn, so a buyer is not handed its open order back (a 200) before its 3 s hold has expired. Every buy must be a `201` (nobody is refused), and `dropped_iterations` must be 0 (k6 could start every arrival). Nobody pays: the holds expire and go back on sale.
- `teardown()` reads both editions back with the creator's token (every route through the gateway needs one): `copiesLeft` is 0, and the copy numbers are exactly 1 to 10, with no gap and no duplicate.
- `late_start_ms` is how many milliseconds after `startAt` each VU was ready. A negative value means it waited for the others, so the burst was simultaneous; a positive p95 means Keycloak was too slow to sign everyone in within 20 s.

The thresholds, and what failing each one means:

| Threshold | Meaning |
|---|---|
| `checks: rate==1` | Every check passed: every winner paid and got a number, the new buyers after expiry got `201`, numbers 1..10 in both editions. |
| `copies_confirmed: count==10` | Exactly 10 buyers won. More is overselling, fewer is a lost copy. |
| `sold_out: count==990` | Every other buyer got a clean `409 sold-out`. |
| `http_req_failed: rate==0` | No 5xx and no connection error. A `409` counts as a good answer here. |
| `server_errors: count==0` | No response was a 5xx, counted on its own. |
| `http_req_duration{name:reserve}: p(99)<BURST_P99_MS` | The burst's reserves (and the expiry scenario's): the slowest 1% took less than `BURST_P99_MS` (3000 ms by default, a soft bound; CI's is 3500). |
| `http_req_duration{name:reserve-steady}: p(99)<P99_MS` | **The rule.** The steady load's `POST /api/orders`: the slowest 1% took less than `P99_MS` (1000 ms by default on the Mac; CI's `P99_MS` is 3500 by the owner's decision, because the runner is slower and noisier, so CI guards against gross regressions only). |
| `dropped_iterations: count==0` | k6 started every arrival the steady load asked for. |
| `login`, `pay`, `fakepay-pay`, `poll` p(99) < 60 s | Not limits: a threshold on a tag makes k6 print that tag's own line in the summary. |

`compose.load.yaml` shortens Inventory's reservation TTL to 3 seconds and its sweep interval to 1 second, so the expiry scenario takes seconds, not 10 minutes. A winner has 3 s between its `201` and its Pay (Pay's first step is a Hold); no run has shown a winner refused as expired, so the TTL stays 3 s. It also switches on FakePay's test endpoints and lets the client `k6` in at the gateway, Orders and Inventory. It is for this test only; a stack started without it keeps the 10-minute TTL.

## The rule, and what the fixes changed (fix round 1, 2026-11-13)

The first three runs below (before the fixes) missed 1 s in the burst. A diagnosis (traces per hop, the kernel's `ListenOverflows`, six experiments) found:

- Every hop queues the 1,010 requests that arrive within 0.3 s at processes that have not served a request yet: Orders fetched the OpenID metadata, the signing keys and its own token on the first buy, and opened its connections to Inventory then.
- Orders' TCP accept queue overflowed (`ListenOverflows` 78 to 483 per run); a dropped SYN is retried after about 1 s, which is the 1 s step in the hop timings.
- In 9 of 18 runs exactly 10 buys got a 503: Orders' client for Inventory uses the standard resilience pipeline, whose concurrency limiter allows 1,000 calls and has a queue of 0 (Microsoft Learn, "Standard resilience handler defaults": "Queue: 0, Permit: 1_000"). That is a bug, not noise.

The fixes: a queue of 1,000 on that limiter in `AddServiceClient` (shared, so Inventory's client for Catalog has it too), a one-shot warm-up that keeps Orders' `/health/ready` unhealthy until the OpenID metadata, the keys, the token and one call to Inventory have worked once, and Kestrel's listen backlog of 4,096 in `AddServiceDefaults` (default 512; the containers' `somaxconn` is 4,096, measured). The owner then decided to split what the rule measures: the 1 s rule is for a steady arrival rate, and the burst keeps every correctness check with a soft latency bound. The gateway has no warm-up: its first requests were not shown to wait for it.

Three runs on the Mac after the fixes, each from a fresh `down -v` and `up -d --build --wait`, then 60 seconds, then k6 with the default thresholds (`P99_MS=1000`, `BURST_P99_MS=3000`). "Load" is the 1-minute load average from `uptime` before k6 started. All three runs: exit code 0, every threshold passed, 7,733 to 7,734 of the same number of checks passing, exactly 10 winners with copy numbers 1 to 10 in both editions, 990 `sold-out`, no 5xx, no dropped iteration, `late_start_ms` negative for every VU, `ListenOverflows` 0 at the gateway, Orders and Inventory after the run, and no `OnRateLimiterRejected` in Orders' log.

| Run | Load | burst reserve p50 / p95 / p99 | steady reserve p50 / p95 / p99 | login p50 / p99 | pay p50 / p99 (20 calls) | poll p50 / p99 | setup |
|---|---|---|---|---|---|---|---|
| 1 | 4.1 | 810 ms / 873 ms / 881 ms | 9.4 ms / 18 ms / **47 ms** | 1.33 s / 2.64 s | 302 ms / 530 ms | 6.6 ms / 20 ms | 4.0 s |
| 2 | 4.7 | 786 ms / 855 ms / 861 ms | 8.9 ms / 29 ms / **219 ms** | 1.25 s / 2.41 s | 293 ms / 597 ms | 7.9 ms / 11 ms | 3.7 s |
| 3 | 3.7 | 938 ms / 996 ms / 1.00 s | 9.0 ms / 15 ms / **25 ms** | 1.31 s / 2.54 s | 310 ms / 585 ms | 5.1 ms / 14 ms | 3.7 s |

Before the fixes the burst's reserve p99 was 1.44, 2.08 and 1.57 s (table below), and 1.2 s in a debugging run. A debugging run of the finished code with loose thresholds, load 6.7, gave a burst p99 of 927 ms and a steady p99 of 254 ms. The burst now fits under 1 s on the Mac in these runs, but it stays a soft bound (3 s) because the diagnosis measured the same code between 1.3 and 3.0 s under other Mac loads. Peak memory (`docker stats`, MiB, highest of the three runs): Keycloak 750, Kafka 527, k6 492 (limit 1 GB), Orders 289, Inventory 264, PostgreSQL 166, Gateway 168, Catalog 126, Payments 120, OpenTelemetry Collector 111, FakePay 56, Redis 9.

## Through the gateway before the fixes (Task 9, 2026-11-13)

Three runs on the Mac, each from a fresh `down -v` and `up -d --wait`, then 60 seconds, then k6 with the default `P99_MS=1000`. "Load" is the 1-minute load average from `uptime` just before k6 started; the Mac was not otherwise idle (other agents' tools, a browser). All three runs: exactly 10 orders `Completed` with copy numbers 1 to 10 in both editions, 990 `sold-out`, expired reservations back on sale, no 5xx, 2,159 of 2,159 checks passing, `late_start_ms` negative for every VU (the burst was simultaneous). **All three missed the rule of 1 s for reserve p99** and the run was reported to the owner without changing a threshold or a pool size.

| Run | Load | login p50 / p95 / p99 | reserve p50 / p95 / p99 | pay p50 / p95 / p99 (20 calls) | poll p50 / p95 / p99 | fakepay-pay p50 / p99 | setup |
|---|---|---|---|---|---|---|---|
| 1 | 6.5 | 1.14 s / 1.93 s / 2.00 s | 1.08 s / 1.36 s / **1.44 s** | 530 ms / 1.04 s / 1.04 s | 4 ms / 14 ms / 15 ms | 1.2 ms / 2.8 ms | 3.6 s |
| 2 | 4.2 | 1.34 s / 2.39 s / 2.44 s | 1.59 s / 2.07 s / **2.08 s** | 522 ms / 999 ms / 1.00 s | 10 ms / 30 ms / 34 ms | 3.2 ms / 5.0 ms | 4.2 s |
| 3 | 7.8 | 1.26 s / 2.13 s / 2.24 s | 1.51 s / 1.55 s / **1.57 s** | 418 ms / 802 ms / 802 ms | 7 ms / 22 ms / 31 ms | 1.4 ms / 6.2 ms | 4.3 s |

One earlier debugging run on a stack that was not freshly created, with `P99_MS=5000`, had reserve p99 1.2 s at load 4.

Peak memory (`docker stats`, sampled in a loop during each run; MiB, highest of the three runs): Keycloak 696 (limit 1 GB), Kafka 528, k6 380, Orders 253, Inventory 238, PostgreSQL 150, Gateway 133, Payments 122, Catalog 112, OpenTelemetry Collector 102, FakePay 52, Redis 8. Nothing was killed. The k6 service's limit is 1g.

The measured `reserve` is now `POST /api/orders` (gateway, Orders, Inventory's reserve, an Orders insert). Before the hop, Inventory alone ran 0.44 to 0.73 s (see below), so the extra hop and the second database cost about 0.7 to 1.4 s at p99 when 1000 buyers arrive in the same instant.

## Results before Task 9 (straight to Inventory)

The sections below measured Inventory directly, before the gateway and Orders were in the path.

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

Every run sold exactly 10 copies with 990 `sold-out`, no failed request and every check passing. Reserve p99 with this change, 0.44 to 0.73 s, is back in the range of the code before Task 3, 0.30 to 0.66 s, and below Task 3, 0.99 to 1.26 s; the medians show the same (376 to 580 ms, 218 to 502 ms, 736 to 988 ms). A run varies by a few hundred milliseconds on this Mac (before Task 3: 295 ms and 657 ms), so compare ranges, not single runs. CI's `P99_MS` is 3500 by the owner's decision (guard against gross regressions).

CI numbers for the same code (head 49cba7a, run 43358510750, three attempts, `P99_MS` 2000 at the time): reserve p99 1.95 s (pass), 1.62 s (pass), 2.75 s (fail at 2000). The runner is slower and noisier than the Mac, so the numbers above and these are not comparable one to one.
