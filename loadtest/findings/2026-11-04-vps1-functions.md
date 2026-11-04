# HTTP functions on vps1, 2026-11-04 (ADR 0029, F13)

Tenant `da-stress`, app `fnload` (`node loadtest/seed/functions.mjs --env vps1`), commit
6ad1fc6 live. k6 ran on vps1 itself, in a container on `dcms_default`, against
`http://content-api:8080` with a spread `X-Forwarded-For`. That leaves out the edge and the
per-address limiter (120 a minute), so the caps under test are the per-app one (3,000 a
minute) and the per-tenant concurrency one (4 HTTP runs per replica, a 2 s wait, then 503).

| Run | VUs | Function | req/s | median | p95 | max | 429 | 503 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 10 | `ping` (returns at once) | 28.7 | 304 ms | 464 ms | 828 ms | 0 | 0 |
| 2 | 30 | `work` (~50 ms CPU) | 22.5 | 1.25 s | 1.53 s | 1.74 s | 0 | 0 |

Through the edge, one cold call each: `ping` 0.63 s, `work` 0.29 s.

## What it says

- **The per-tenant cap is the ceiling, as designed.** Fastest calls take 69 ms (`ping`) and
  128 ms (`work`): a Deno process plus the hop to admin-api. Four slots give about 4 ÷ 0.13 s
  ≈ 30 req/s for `ping` and ≈ 23 req/s for `work`, which is what both runs reached. Past
  that, calls wait at the gate. The wait (≈1.2 s at 30 VUs) is latency, not errors.
- **No 503 at 30 VUs.** The gate's 2 s wait outlasted the queue. More VUs, or slower
  functions, would turn the excess into 503s, which is the intended behaviour.
- **Host:** load average reached 9 on 4 cores during run 2, and was back to 1.5 within
  4 minutes. One tenant's functions can take most of the box's CPU, even within the cap.
  `ScriptRunner` slots and the per-tenant cap are the knobs: lower the cap before giving
  the runner more slots.

## Next, if HTTP functions need more

A process per call is the cost floor. To go past about 30 req/s per tenant per replica, either
reuse warm isolates per deployment (a pool keyed by bundle hash) or add admin-api replicas,
since the cap counts per replica.
