# Load and stress testing: every test, every change, every effect

One page over the three campaign reports ([2026-09-02](2026-09-02-vps1.md),
[2026-09-24](2026-09-24-vps1.md), [2026-09-26](2026-09-26-vps1-stress.md)) and the 2026-09-27
re-run. Target vps1 (4 cores, 7.6 GB), generator VPSM, over the public internet (~20 ms RTT).
Each row says what a test found, the commit that changed it, and what the next measurement
showed. "Not re-measured" means exactly that.

## Where the platform stands now

| surface | first measured | now | what changed it |
|---|---|---|---|
| content delivery API | 380 req/s, **collapsing to 164 req/s with 9.5 % errors** at 200 VUs | **510 req/s, 0 % errors** at 200 VUs; 470 at 400 | pool caps, tenant cache |
| hosted sites | ~310 req/s ceiling, page p95 1.7 s at 400 VUs | **~540 req/s**, page p95 184 ms at 400 VUs | artifact cache |
| admin API, 200-item tenants | 329 req/s, **collapsing to 192** at 240 VUs | **~500–526 req/s**, 0 % errors at 240 VUs | pool caps, tenant cache |
| admin API, 6,400-item tenant | 131 req/s; **46 % errors, 3 OOM crashes** at 240 VUs | **~310 req/s, 0 % errors, 0 restarts** at 240 VUs | content paging, audit RLS plans |
| publish → visible | 204 req/s at 48 VUs, 1.05 % errors, p95 1.4 s | **319 req/s, 0 %**, p95 445 ms | pool caps |
| mixed (realistic) | 225 req/s, delivery p95 **3.8 s** | **330 req/s**, delivery p95 151 ms | all of the above |
| media queue → ready (4 uploaders) | p95 **17.5 s** | p95 **3.8 s** | parallel consumers, lossy WebP |
| login | ~650 ms | **187–300 ms** (the rest is PBKDF2 + TLS) | Forgejo sync off the request |
| Mode C site publish → live | 2.1 s | 2.2 s at 16 concurrent builds; ceiling not reached | — |

vps1 is now **CPU-bound everywhere**: at each ceiling the host sits at 85–99 % CPU with no
container throttled. Telemetry (Alloy + Tempo) takes 0.5–0.65 cores of that, the TLS edge
0.4–0.9.

---

## Campaign 1 — 2026-09-02: first baseline (harness `da842d9`)

**Tests:** `sitehost`, `admin`, `media`, `sitebuild`, then the full suite of 8 scenarios
(15 runs, ~400,000 requests). Everything ran at 0 % errors at baseline load (216 site req/s,
401 admin req/s), so this round found defects, not limits.

| # | finding | change | effect, measured |
|---|---|---|---|
| 1 | Login was 78 % a synchronous `PATCH` to Forgejo (421 of 539 ms), on every sign-in | `befea0a`: sync deferred to the outbox; `ffef54b`: only when the mirror is stale | login **~650 → ~300 ms**, then **187–283 ms** with **0** outbox writes per login (was 1) |
| 2 | Every JetStream consumer handled **one message at a time**; `MaxAckPending` was only prefetch. Media used 1 of 4 cores while the queue grew | `befea0a`: `Parallel.ForEachAsync`, `MaxConcurrency` per consumer (media and site-builder) | media queue p95 **17.5 → 6.4 s**, p50 6.4 → 2.4 s, **87 → 194** assets in 3 min, worker 1.0 → 2.5 cores. 2.2×, not 4×, because the host became the limit |
| 3 | site-builder and email-worker exported **no telemetry at all** (a least-privilege compose split dropped the OTEL variables) | `befea0a`: separate `otel-env` anchor; `obs-smoke.sh` asserts per service | site-builder **0 → 337** series, email-worker **0 → 197**; the site-build dashboard has data for the first time |
| 4 | `dcms:http_requests:rate1m` empty since written (60 s OTLP export vs a 1 m `rate()` window); 6 panels blank | `befea0a`: rule window 2 m, same name | 0 → 4 series; 6 panels live |
| 5 | Rate limit on the read path made the `publish` scenario report 18 % errors / 6 "never visible" | harness: scenario accounting (`37cbf18`) | same scenario **0.00 %**, 0 never visible; publish→visible p50 **69 ms**, p95 122 ms |
| 6 | Unpaged `GET /api/admin/content` suspected (admin 401 → 334 req/s after the tenant grew) | not changed then; needed a big tenant to prove | settled on 2026-09-26, below |
| 7 | Postgres's heaviest statements were its own exporter's; vps1 disk 75 %, ~48 GB of it reclaimable Docker | noted only | — |

**Round 4, 2026-09-03 — render modes and dashboards**

| # | finding | change | effect, measured |
|---|---|---|---|
| 8 | Build cost per mode: C 367 ms, A 1.46 s, B **36.95 s** (p95 publish→live) | measured (`dab2909`, `51b4e8c`) | — |
| 9 | One Mode B build blocked every other publish: Mode C p95 **367 ms → 34.8 s** (95×) when mixed | `93cb397`: one publish subject and queue lane per render mode | **not re-measured** |
| 10 | Mode B broken on vps1, silently: the sandbox image was missing (`docker image prune -a` deletes it) | `scripts/deploy.sh` checks the tag resolves, fails the deploy otherwise | both Mode B fixture sites built first time after the next deploy |
| 11 | **77 of 224 Grafana panels returned nothing** (wrong metric names, missing scrapes, Vault retention 30 s < 60 s scrape, un-incremented counters) | dashboards, scrapes, `4b07aca` (Vault) | **77 → 13** empty, all 13 explained and labelled |
| 12 | Build reaper (15 min) shorter than a Mode B build's own timeout (18 min) | `0bda7b3`: 25 min | — |

## Campaign 2 — 2026-09-24: first suite after the edge BFF and RLS, with profiling

**Tests:** full suite at baseline VUs, plus a Pyroscope CPU profile per service per run.
All scenarios 0 % errors once the harness could get through.

| # | finding | change | effect, measured |
|---|---|---|---|
| 13 | Harness could not sign in: the edge BFF strips client `Authorization` | `85a307e`: sign in through the edge like a browser | admin scenarios run again; no 8-minute token cap |
| 14 | Edge's per-IP rate limit: delivery at 10 VUs was **97 % 429s** | `6446858`: rate-limit exemptions in the platform console, applied live via NATS | exempt: 42,921 admitted, **0 refused**; removed: **98.1 % refused**, exactly 600/min |
| 15 | The load made **Tempo OOM-loop 58 times** (Alloy retry backlog + compaction of burst blocks) | `0a2cba0`, `a990487`: Tempo ingestion 2 MB/s, Alloy queue 100 / retry 1 min, compactor block cap | **0 Tempo restarts** through every later campaign run but one (09-26, exit 0, not OOM) |
| 16 | Admin **~25 % slower** (334 → 252 req/s, audit p95 86 → 148 ms): RLS changed the list plans | analysed; direction chosen on 09-27 | fixed 2026-09-27, below |
| 17 | `set_config` round trip before **every** command (453,795 calls, top statement) | open | — |
| 18 | WebP ladder encoded **lossless by accident** (~75 % of media-worker CPU) | `bbcbf6e` (09-26): `FileFormat = Lossy` | media queue p95 **8.1 → 3.8 s** |

## Campaign 3 — 2026-09-26: stress staircases, to find the limits

**Tests:** every scenario as a staircase (`stress.sh`, 2-minute steps at rising VUs, stopping
at the knee), generator exempted throughout. 2 of 7 surfaces **collapsed** under overload
instead of levelling off.

| # | finding | change | effect, measured |
|---|---|---|---|
| 19 | **Postgres connection exhaustion.** No pool cap: each service could open 100 against `max_connections=100`. `53300` errors (1,212 in one content-api window), 95/100 connections, load average 49 | `91471c3`, `37442a3`: `Maximum Pool Size` per service, 90 of 97 slots (admin-api 35, content-api 25, …) | delivery at 200 VUs **164 → 510 req/s, 9.5 → 0 % errors**; admin at 240 VUs **192 → 502 req/s**; publish at 48 **204 → 319 req/s, 1.05 → 0 %** |
| 20 | Tenant looked up in Postgres on **every request** (~680k calls) | `91471c3`: 30 s cache of hits; `03ac3db`: evict on create/delete too (the campaign's own reseed exposed the missing eviction) | Postgres CPU in the delivery staircase **0.84 → 0.3 cores**; part of the 380 → 510 delivery gain. 3 integration tests |
| 21 | Hosted sites: every page view = 2–4 **signed MinIO calls**; MinIO the top CPU consumer (1.09 cores) | `3d558de`: `SiteArtifactCache` (per-build, no invalidation needed, 64 MB, ≤ 256 KB bodies) | **305 → 531 req/s** at 100 VUs, page p95 **449 → 92 ms**; at 400 VUs **306 → 539**, page p95 **1.7 s → 184 ms**; MinIO 1.09 → 0.47 cores, host 88 → 75 % CPU |
| 22 | On a 6,400-item tenant the picker route `GET /api/admin/content` held every draft in memory: **3 admin-api OOM crashes, 46 % of admin requests 502** for every tenant | `4bc9593`: paged, `{ items, nextCursor }`; picker/preview follow the cursor | at 240 VUs **46 % → 0 % errors, 3 → 0 restarts** |
| 23 | The paged console list still cost **537 ms per page** (titled every item before taking 50) | `6faec27`: index `ix_content_items_collection_recent`, find the page first; `a3cb8f3`: dress by primary key | **1.2 ms** per page (`EXPLAIN ANALYZE` as `dcms_app`); admin **131 → 217 req/s** at 60 VUs, content p95 1.6 s → 491 ms |
| 24 | Alloy's rejected-span lines filled the collector's log query, hiding 6,418 real errors | `37442a3`: collector excludes Alloy lines, saves per-service counts | bundles carry the real errors |
| 25 | Orphaned JetStream consumer showing a permanent 147-message backlog | `91471c3`: deploy retires it | backlog gone |
| 26 | Outboxes never pruned (190,905 + 32,162 rows) | open | — |

## 2026-09-27: the audit list's RLS plans (finding 16)

**Test:** `EXPLAIN ANALYZE` on a local Postgres with vps1's data shape (8 × 15,000 tenant rows,
174,000 platform rows), as `dcms_app`. Reading vps1's own database was not permitted.

- **Cause:** in platform scope the RLS qual `current_setting('app.scope') = 'platform'` names no
  column, so Postgres estimated it at 0.5 %. It expected 74 of a tenant's 14,785 rows and chose
  fetch-everything-and-sort. The query's own `OR` (own rows, or platform rows about members)
  also prevented a newest-first index walk.
- **Change** (`f03b31b`): the `platform_scope` policy calls `public.dcms_platform_scope()`,
  a plpgsql function estimated at 33 %, on every RLS table and partition; the audit list reads
  two branches, each ordered and limited, joined by `UNION ALL`, with an index-usable cursor.
  Two new integration tests.
- **Effect, locally:** one page **25 ms → 0.6–1.1 ms**, first page or deep cursor.
- **Effect on vps1** (same 6,400-item seed and staircase as the run before it):

| VUs | before | after |
|---|---|---|
| 60 | 217 req/s, audit p95 1,275 ms | **311 req/s**, audit p95 **458 ms** |
| 120 | 214 req/s, audit p95 1,735 ms | **309 req/s**, audit p95 **774 ms** |
| 240 | 216 req/s, audit p95 2,520 ms | **308 req/s**, audit p95 **1,369 ms** |

0 % errors, 0 restarts. Every admin endpoint's p95 now rises together, so the remaining
limit is CPU, not one query.

---

## Cumulative effect on the big-tenant admin plane

| step | admin req/s at 240 VUs, 6,400-item tenant | errors |
|---|---|---|
| before any fix | 134 | **46 %**, 3 crashes |
| + picker paged, list indexed (`4bc9593`, `6faec27`, `a3cb8f3`) | 216 | 0 % |
| + audit RLS plans (`f03b31b`) | **308** | 0 % |

**2.3× the throughput, from failing to clean**, on the same hardware.

## Still open, in order of value per effort

1. **One `set_config` per connection checkout**, not per command (finding 17): a round trip
   off every query on every RLS service, 15–21 ms each under load.
2. **Prune the outboxes** (finding 26).
3. **Tail sampling under overload**: roughly half a core back at saturation.
4. **Faster PNG re-encode in the media sanitizer** (~20 % of media-worker CPU).
5. **Re-measure finding 9** (per-mode build lanes, `93cb397`): shipped but never re-tested.
6. The registry cleanup policy (keep `dev`, `stable`, `v*`, `mirror` tags) — needs a GitLab
   admin change.
