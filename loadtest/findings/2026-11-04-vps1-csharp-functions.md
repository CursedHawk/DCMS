# C# vs TypeScript HTTP functions on vps1, 2026-11-04 (ADR 0030)

Tenant `da-stress`, app `fnload`, deployment 5 (`node loadtest/seed/functions.mjs --env vps1`), commit
a2bc2b7 live. Same setup as [the TypeScript run](2026-11-04-vps1-functions.md): k6 on vps1 in
a container on `dcms_default`, against `http://content-api:8080` with a spread `X-Forwarded-For`.
The cap under test is the per-tenant one: 4 HTTP runs at once per replica, a 2 s wait, then 503.

| Function | VUs | req/s | median | p95 | min | 503 |
| --- | --- | --- | --- | --- | --- | --- |
| `ping` (TS) | 10 | 28.3 | 294 ms | 492 ms | 71 ms | 0 |
| `cs-ping` (C#) | 10 | 3.6 | 2.55 s | 3.11 s | 890 ms | 33 |
| `work` (TS, ~50 ms CPU) | 30 | 22.5 | 1.23 s | 1.59 s | 127 ms | 0 |
| `cs-work` (C#, ~50 ms CPU) | 30 | 11.2 | 2.01 s | 3.44 s | 1.07 s | 709 |

Load average peaked at about 7.8 on 4 cores in every run.

## What it says

- **The floor of a C# call on vps1 is about 0.9 s**, against 71 ms for TypeScript. It is one Deno process
  booting .NET's WebAssembly runtime (~50 assemblies, Mono initialisation) and interpreting
  the first call. Locally the same boot is about 0.5 s, so vps1's CPU is roughly half as fast.
- **The per-tenant cap turns that into the ceiling.** 4 slots ÷ 0.9 s ≈ 4.4 req/s, which is what
  `cs-ping` reached. Past it, calls queue for the gate's 2 s and then get 503, as designed.
  `cs-work` shows more 503s and a higher rate only because its 503s are fast answers.
- **C# is fine for triggers, jobs and low-rate HTTP. It is not yet fine for a busy public endpoint.**
  Every functional check passed: answers, POST bodies, the build in the real image, deploy.

## Next, if C# HTTP functions need more

The cost is the boot, not the code. In order of payoff:

1. **Keep a warm .NET per live deployment.** The boot is paid once and the assembly is already
   loaded, so a run becomes one call. The price is isolation between runs: statics survive from one
   run to the next within the same deployment's own code, as on any serverless platform's warm instance. That
   is a decision to make, not a default (kanban).
2. Trim the curated BCL and the host's own start-up (fewer assemblies to load per boot).
3. More slots or admin-api replicas: these scale linearly, at the cost of the box's CPU.

## After the warm runtime (#607, 3e3c994, 2026-11-05)

The same setup, with a deployed C# function's process kept for that deployment's runs:

| Function | VUs | req/s run | median | p95 | 503 | 429 |
| --- | --- | --- | --- | --- | --- | --- |
| `cs-ping` | 10 | ≈59 (21,365 answered in 85 s) | 14 ms | 113 ms | 0 | 16,358 |
| `cs-work` (~50 ms CPU) | 30 | 48.7 | 550 ms | 776 ms | 0 | 0 |

- **`cs-ping` now meets the per-app cap (3,000 calls a minute, 429), not the runtime.** Of the
  calls that ran, the median is 14 ms, faster than TypeScript's 294 ms (TypeScript still starts a Deno per call).
- **`cs-work`** went from 11 req/s with 709 × 503 to 48.7 req/s with none. It is CPU-bound now: load
  average 12 on 4 cores, with k6 on the same box.
- Next, if TypeScript HTTP functions need the same: the identical keep-warm path, keyed by the
  bundle's hash, would apply to `session-harness.js`.
