# 2026-11-27 vps1: WebSockets hub (ADR 0037)

Target `test.highgeek.eu` (tenant test1, app `rt`, keyed channel `chat`), scenario
`scenarios/websockets.js`: 500 connections in 25 rooms of 20, each publishing every 10 s, each
connection held 120 s and reopened. k6 ran **on vps1 itself** (this box had no memory to
spare), capped at 1.2 GB, so the latency includes a generator sharing the 4 cores.

| | |
| --- | --- |
| upgrades | 1214, 100 % 101 |
| published / delivered | 10 884 / 204 592 (≈ 690 deliveries/s) |
| delivery p50 / p95 / max | 3 / 6 / 65 ms |
| invocation errors | 0 |
| content-api memory | 242 → ~297 MiB at the plateau (≈ 110 KiB per connection), limit 640 MiB |
| content-api CPU | ≤ 27 % |
| edge memory / CPU | 280 → 296 MiB, ≤ 22 % |
| Redis | flat (4–6 MiB) |

Read: at this size nothing is near a limit. Extrapolated, content-api's 640 MiB cap holds
about 3 000 connections per replica; `MaxConnectionsPerTenant` (2000 per replica) stops a
single tenant before that. Memory was not returned after the run (288 MiB, GC keeps it).

Also checked by hand the same day: origin, admin host and unknown instance refused;
schema/key/access refusals; a function's `send` on admin-api reaching a browser through the
`dcms-ws` backplane (≈ 2.8 s including its build); disabling the app drops open connections
at their next call (53 ms) and refuses new ones.
