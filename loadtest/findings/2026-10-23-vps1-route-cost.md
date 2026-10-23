# What each request costs the host — 2026-10-23, vps1

After the 2026-10-22 fixes vps1 is CPU-bound again (delivery ~424, admin ~370 req/s). The staircases
cannot say *why*: at saturation every request queues, so latency stops meaning cost (an audit read at
240 VUs spent 824 ms in admin-api, 158 ms of it in SQL). This campaign measures cost instead.

**Method.** New scenario `scenarios/routecost.js`: 21 routes, each alone at a fixed rate well below the
knee (40 req/s; writes and the analytics dashboard 8/s) for 60 s, 15 s apart, after a 60 s idle
baseline. `collect/cpusample.sh` reads every container's cgroup CPU and `/proc/stat` every 5 s on the
host (cAdvisor is scraped every 30 s and node CPU every 60 s, too coarse), and
`collect/routecost.mjs` turns that into **CPU-ms per request per container**, idle subtracted. Then per
route window: 15 Tempo traces rooted at the edge (span tree, SQL per request) and a Pyroscope
flamegraph per service; pg_stat_statements was reset just before the run, so the bundle's SQL is this
run's alone. master `8969ac3`, 47,040 requests, 0 failed.

Bundle: `runs/20261023T090237Z-routecost-vps1` (`routecost.txt`, `traces-<route>.jsonl`, `prof/`).

## The bill

CPU-ms per request, whole host. The host total equals the sum of the containers within noise, so
nothing meaningful hides in the kernel. `tel` = Alloy + Tempo + Loki.

| route | host | admin-api | content-api | postgres | edge | site-host | minio | jobs-worker | tel |
|---|---|---|---|---|---|---|---|---|---|
| admin_publish (create + publish) | **75.5** | 23.6 | 6.1 | 14.8 | 6.3 | — | — | 9.7 | 14.4 |
| admin_analytics (dashboard) | **35.6** | 18.3 | — | 14.5 | 2.5 | — | — | — | 4.1 |
| da_get_expand | **28.8** | 8.6 | — | 14.5 | 3.1 | — | — | — | 2.8 |
| da_list | **28.7** | 8.1 | — | 15.8 | 4.0 | — | — | — | 1.6 |
| da_site_anon | **26.8** | — | 7.7 | 9.9 | 2.7 | 2.0 | — | — | 4.3 |
| delivery_media | 20.6 | — | 6.5 | 1.0 | 2.1 | 1.8 | 4.5 | — | 3.8 |
| site_asset | 20.5 | — | — | — | 8.6 | 5.4 | 5.9 | — | 1.1 |
| admin_audit | 18.7 | 6.0 | — | 7.6 | 5.1 | — | — | — | 0.8 |
| analytics_collect | 17.0 | — | 4.0 | 1.6 | 1.9 | 1.9 | — | 3.0 | 4.2 |
| admin_picker | 16.1 | 6.8 | — | 2.6 | 6.2 | — | — | — | 1.6 |
| delivery_list | 13.8 | — | 4.2 | 0.5 | 3.3 | 2.1 | — | — | 2.3 |
| admin_content_page | 13.2 | 5.2 | — | 3.4 | 3.8 | — | — | — | 1.4 |
| delivery_openapi | 12.9 | — | 4.0 | 0.5 | 2.8 | 2.0 | — | — | 2.7 |
| admin_notifications | 11.7 | 4.7 | — | 1.8 | 3.4 | — | — | — | 3.1 |
| delivery_item | 10.9 | — | 3.5 | 0.8 | 2.1 | 1.9 | — | — | 1.7 |
| admin_media | 10.3 | 3.5 | — | 2.5 | 3.2 | — | — | — | 1.8 |
| delivery_tags | 10.1 | — | 3.2 | 0.5 | 2.0 | 1.8 | — | — | 2.2 |
| admin_instances / sites | 5.6 | 3.0 | — | 0.3 | 2.7 | — | — | — | 0.8 |
| admin_permissions (Redis-cached) | 3.9 | 1.4 | — | — | 2.7 | — | — | — | 0.6 |
| site_page (static) | **2.1** | — | — | — | 1.9 | 0.8 | — | — | 0.8 |

Read across: a static page costs 2 ms; the same tenant's **cached** JSON costs 11–14 ms. The admin
floor is ~4 ms (edge session + permission cache). Everything above the floor is one of the causes
below, most of them shared by every route.

## Why — ranked by host CPU they would return

### 1. Telemetry is ~14 % of the host before the services' own share — `infra/observability/alloy/config.alloy:80`

Alloy + Tempo + Loki are **14.0 %** of all host CPU across the run, 17–27 % on the cheap high-volume
routes and 37 % of a static page. Sampling is off by design (`always_sample`), every span of every
request goes through the 20,000-trace tail buffer, Tempo's span-metrics/service-graph generator, and
storage. Inside the services it is another **6–13 %** (content-api 9.8, edge 12.7, admin-api 7.4) and
**30 % of site-host** (bucketed flamegraphs, delivery step 2026-10-22). The parts:

- **site-host logs two lines per proxied request.** `src/Services/Dcms.SiteHost/appsettings.json:7`
  lacks the `"Yarp": "Warning"` override the edge has, so YARP's "Proxying to http://content-api…" and
  "Received HTTP/1.1 response 200" are written for every delivery request — 9,080 lines in ten
  minutes, 12× the next service — through Serilog to stdout *and* OTLP, i.e. twice into Loki. Serilog
  plus the OTLP log exporter is ~20 % of site-host's CPU. **Fix: one line.**
- **`set_config` and other low-value spans.** Every RLS round trip (§2) is its own Npgsql span — 15 of
  34 spans on the analytics dashboard, 2–5 on most routes — and each costs again in Alloy and Tempo.
  **Fix:** Npgsql 10's tracing command filter (`ConfigureTracing(o => o.ConfigureCommandFilter(...))`
  on the data source) to skip the `set_config` statement.
- **`SensitiveAttributeProcessor` scans every tag of every span with 11 case-insensitive `Contains`**
  (`src/Shared/Dcms.Shared.Telemetry/SensitiveAttributeProcessor.cs:37`) — 2.2 % of content-api, 1.8 %
  of the edge, and the hottest single self-time frame in content-api (`IndexOfIgnoreCase`). Tag
  *names* are a fixed vocabulary. **Fix:** memoise the verdict per name.
- **Sampling** is the big lever and a policy call, not a bug: the config's own comment trades CPU for
  "every trace id resolves". Keeping errors + slow (>1 s) + 10 % baseline would cut most of the 14 %.
  Needs a decision.

### 2. Every connection open is an extra round trip — `src/Shared/Dcms.Shared.Data/Rls/TenantGucInterceptor.cs:39`

EF opens and closes the connection per query, and the RLS interceptor runs
`SELECT set_config(...)` on every open: **103,831 of them for 47,040 requests** (2.2 per request; 15 on
the analytics dashboard, 4–5 on Dynamic Apps). Postgres barely notices (0.01 ms each) — the cost is the
awaited client round trip, its socket syscalls and its span: `ApplyAsync` is **10–14 %** of
content-api, admin-api and jobs-worker CPU in every window profiled. Npgsql's pool reset adds a
`DISCARD ALL` per open (104,329), cheap because it rides on the next statement. Since 2026-08-26 the two
together are 44 % of every statement Postgres has executed.

**Fix (needs a design pass, RLS is a security boundary):** fewer opens, not a weaker reset — open each
context's connection once per request scope (one `set_config` per context, not per query), or carry the
GUC on the first command's round trip. Do *not* reach for `No Reset On Close`: a raw connection that
bypasses the interceptor would then inherit the previous request's tenant instead of none.

### 3. Every plugin request reloads every enabled plugin instance — `src/PluginSdk/Dcms.PluginSdk.Runtime/Contracts/CmsPluginInstanceStore.cs:16`

`PluginInstanceFilter` → `PluginContextFactory.EnabledInstancesAsync` (memoised per request only,
`PluginContext.cs:36`) → `ListEnabledAsync` reads all of a tenant's enabled instances with
`ConfigJson` and `JsonDocument.Parse`s each one — **15.6 % of admin-api CPU** on Dynamic Apps reads,
6 % on publish. content-api does its own uncached lookups of the same table on every delivery request
(`DeliveryEndpoints.cs:74` by slug, `OpenApiEndpoints.cs:80` all instances). 15,378 queries in the run,
each with its own open + `set_config`.

**Fix:** a process cache per tenant, invalidated by a generation counter bumped when an instance
changes (the pattern delivery's list cache already uses). The cached `JsonDocument`s become shared, so
no caller may dispose them.

### 4. EF Core's per-context and per-query overhead — `src/Shared/Dcms.Shared.Data/*/…ServiceCollectionExtensions.cs`

Before any SQL: building a fresh internal service scope per context (`get_ContextServices` 7.5–8.7 %),
re-running each registration's options lambda per request (`CreateDbContextOptions` 4.8–5.7 %), and
the constructor (2.6–3.6 %). Per query: parameter extraction (`ExpressionTreeFuncletizer`) 6 % of
content-api and 12 % of admin-api. EF Core is 16–21 % of every EF service's CPU. Also
`OutboxAuditSink` takes `AuditDbContext` in its constructor, so every request builds one even when
nothing is audited.

**Fix (refactor):** `AddPooledDbContextFactory` / `AddDbContextPool` removes the per-context scope and
options work, but the interceptors are scoped (`ITenantContext`), so they must read the tenant at
execution time instead of capturing it. `EF.CompileAsyncQuery` for the handful of hottest queries
(instance lookup, record by id). Lazy `AuditDbContext` in the sink.

### 5. Dynamic Apps: a model snapshot and a count on every request — `src/Plugins/Dcms.Plugins.DynamicApps/Data/RuntimeModel.cs:144`, `RecordService.cs:119`

- `PublishedModels.LoadAsync` caches the compiled model by revision id, but to *find* that id it
  selects the whole revision row including `Snapshot` — **164 KB** for the da-stress CRM, **5.5 MB** for
  the largest app — then discards it on a cache hit. 7,213 times in the run; 10.5 % of admin-api CPU
  on `da_get_expand`. **Fix:** select `Id, Number, Hash`; load `Snapshot` only on a miss.
- Every list request also runs the capped `COUNT` (up to 5,001 rows), the **largest single DB cost of
  the whole run** (4,792 × 2.2 ms), on top of the page query (`ORDER BY (Data->>…)::numeric`, 4 ms).
  **Fix:** count only when the client asks (first page / explicit `count=true`), or cache it per table
  under the records' write generation.

### 6. The delivery path does work the cache was meant to skip

- **Three hops.** `edge → site-host → content-api` for every `/api` call on a tenant domain: site-host
  costs 1.8–2.1 ms and the edge 2–3 ms per request, plus their spans. Larger fix: let the edge route
  tenant-domain `/api/*` straight to content-api with the tenant header site-host's `ApiProxy` adds today.
- **Media is five MinIO round trips** (`ObjectStreaming.cs:114`): the SDK `HEAD`s the bucket before
  each call (no region configured, `StorageServiceCollectionExtensions.cs:17`), then our `StatAsync`
  `HEAD`s the object, then `GetObjectAsync` repeats both before the `GET`. Plus 3 queries on 3 opens.
  **Fix:** `.WithRegion(...)` on the client (−2), use the size/content type the variant row already
  holds instead of `StatAsync` (−1 or −2).
- **OpenAPI pulls the whole gzipped document out of Redis to answer a 304** (`OpenApiEndpoints.cs:100`),
  after reading every instance and parsing every config to compute the key. **Fix:** cache the hash per
  tenant under the instance generation (§3); compare the ETag before fetching the blob.
- **`/api/tags` reads `userauth.api_rules` on every request** (`TagDeliveryEndpoints.cs:77`) — the same
  rows `UserApiAccess` already holds in memory for 10 s. **Fix:** read them from there.

### 7. Smaller, but measured

- **Analytics dashboard**: 15 aggregate queries over raw `analytics.events`, each on its own connection
  (15 `set_config`s, 30 of 34 spans are SQL). Already the open item from 2026-10-22: count from rollups.
- **Audit list** still costs 8 ms of SQL: the member branch sorts all platform rows about the tenant's
  members, exactly as its `ponytail:` note says (`AuditEndpoints.cs:277`); the LATERAL-per-member walk it
  names is now worth doing.
- **Outbox pollers** scan dead index entries to find an empty queue: `audit_outbox` 327 buffers / 3.9 ms,
  `apps.outbox` 867 buffers / 2.4 ms, ~1,650 polls each per half hour. Per-table autovacuum settings on
  the two queue tables.
- **Analytics ingest** opens a scope, transaction and `set_config` per beacon (one message = one event):
  3 ms of jobs-worker per beacon. Batch several messages per transaction.
- **Notifications** seq-scan `recipients` for the newest page and the unread count (13 ms / 1.3 ms
  today, linear in the user's notifications): the index puts `ReadAt` before `CreatedAt`
  (`NotificationsDbContext.cs:151`). `(TenantId, UserId, CreatedAt DESC) WHERE DismissedAt IS NULL`.
- **Publish** (75 ms a pair) has no single hot spot — audit chain append, notification burst join,
  outbox, NATS, cache invalidation; it gets cheaper with §1–§4.

## Suggested order

| | fix | size | expected |
|---|---|---|---|
| 1 | site-host `Yarp: Warning` | 1 line | ~20 % of site-host, three quarters of the container-log lines Loki takes in |
| 2 | memoise `LooksSensitiveName` per tag name | ~5 lines | ~2 % of every service |
| 3 | Dynamic Apps: don't select `Snapshot` on a cache hit | ~10 lines | ~10 % of admin-api on DA reads, 164 KB–5.5 MB per request off the wire |
| 4 | MinIO `WithRegion` + no `StatAsync` when the row knows the size | small | media 5 → 2 MinIO calls |
| 5 | cache enabled plugin instances per tenant (generation-keyed) | medium | ~15 % of admin-api on plugin routes, 1 query off every delivery request |
| 6 | skip `set_config` spans in Npgsql tracing | small | 2–15 spans per request out of the pipeline |
| 7 | Dynamic Apps count on demand | medium (API shape) | the largest DB statement of the run |
| 8 | one connection open per context per request | design | most of 10–14 % RLS overhead |
| 9 | trace sampling policy | decision | most of the 14 % telemetry pipeline |
| 10 | DbContext pooling / compiled queries | refactor | a share of EF's 16–21 % |
| 11 | edge → content-api directly for tenant `/api` | medium | ~4 ms and 4 spans per delivery request |

## Fixed — 2026-10-23

| | fix | where |
|---|---|---|
| 1 | site-host's YARP per-request Information logs off | `SiteHost/appsettings.json` |
| 2 | sensitive-name verdict memoised per tag name | `SensitiveAttributeProcessor` |
| 3 | Dynamic Apps reads the revision id, the snapshot only on a cache miss | `PublishedModels.LoadAsync` |
| 4 | media and large site assets stream from **one** presigned GET (minio-dotnet 7 sent a bucket-exists HEAD before every call and a stat inside GetObject: five requests; `.WithRegion` changes none of that, measured); ranges are a real ranged GET | `IObjectStorage.OpenReadAsync`, `ObjectStreaming` |
| 5 | enabled plugin instances cached per process under a Redis generation every instance write bumps; delivery and OpenAPI read them there; an OpenAPI 304 no longer pulls the document out of Redis | `PluginInstanceCache` |
| 6 | the RLS GUC is no longer its own span | `TenantGucInterceptor` |
| 7 | Dynamic Apps totals: none counted for a short page; full-page counts cached per filter under a per-app generation bumped after each committed write (decided: cache, API unchanged) | `RecordCounts` |
| 8 | the RLS GUC travels **in front of the command** (`SET …; SET …;` prepended) instead of a separate round trip per open; separate only for migrations and SaveChanges batches (EF checks row counts by statement position). A failed command or rollback forgets what was set; raw ADO commands call `TenantGucInterceptor.Prepare`, enforced by `RawCommandRlsCoverageTests` | `TenantGucInterceptor` |
| 10 | DbContexts pooled: `ScopedDbContext` binds the tenant per lease and drops it on return; the RLS and audit interceptors are singletons that read the request through the context | `ScopedDbContext`, `AddDcmsDbContext` |
| 11 | tenant `/api` and `/hub` go edge → content-api; content-api resolves the Host with site-host's own `DomainResolver` (moved to `Dcms.Shared.Sites` with its invalidators), the edge still knows nothing about tenancy (ADR 0010's "version worth building") | `SiteHostResolution`, `PlatformRoutes` |
| §6 | `/api/tags` reads API rules from the 10 s cache the request gate already keeps | `ApiRuleVisibility.RulesAsync` |
| §7 | outbox queue tables vacuum after 2,000 dead rows (34,361 dead entries were being walked to find an empty queue); notification inbox and unread indexes; `AuditDbContext` built only when something is written | migrations, `OutboxAuditSink` |

Re-measured the same afternoon on `8cda48d` (deployed 14:49 UTC), same settings, 47,054 requests,
0 failed (`runs/20261023T145813Z-routecost-vps1`). CPU-ms per request, before → after:

| route | host | content-api | admin-api | postgres | site-host | minio |
|---|---|---|---|---|---|---|
| da_list | 28.7 → **19.8** | — | 8.1 → 6.3 | 15.8 → **8.0** | — | — |
| da_site_anon | 26.8 → **18.4** | 7.7 → 5.6 | — | 9.9 → 5.1 | 2.0 → 0 | — |
| da_get_expand | 28.8 → **23.4** | — | 8.6 → 5.8 | 14.5 → 12.5 | — | — |
| delivery_media | 20.6 → **12.9** | 6.5 → 3.5 | — | — | 1.8 → 0 | 4.5 → 2.0 |
| site_asset | 20.5 → **15.4** | — | — | — | 5.4 → 3.1 | 5.9 → 3.1 |
| analytics_collect | 17.0 → **10.5** | 4.0 → 2.7 | — | 1.6 → 1.2 | 1.9 → 0 | — |
| delivery_item | 10.9 → **6.2** | 3.5 → 2.1 | — | 0.8 → 0.2 | 1.9 → 0 | — |
| delivery_tags | 10.1 → **6.2** | 3.2 → 1.7 | — | 0.5 → 0.1 | 1.8 → 0 | — |
| delivery_list | 13.8 → 13.1 | 4.2 → 3.0 | — | 0.5 → 0.2 | 2.1 → 0 | — |
| delivery_openapi | 12.9 → 14.5 | 4.0 → 2.8 | — | — | 2.0 → 0 | — |
| admin_analytics | 35.6 → 37.8 | — | 18.3 → 14.1 | 14.5 → 15.5 | — | — |
| admin_publish | 75.5 → 73.0 | 6.1 → 3.7 | 23.6 → 21.0 | 14.8 → 13.8 | — | — |

**Weighted over every request: 15.4 → 13.2 CPU-ms (−15 %).** The services the fixes touched fell
30–50 % (content-api on delivery, MinIO, Postgres on Dynamic Apps lists) and site-host left the
delivery path. The host totals move less than that on light routes, and a few rose (openapi,
admin_media, site_page): in those rows the services are flat or down and the difference is Tempo
(openapi's 1.6 → 4.0 ms), whose cost per request swings with its flush and compaction timing, plus
the ~±2 ms idle-subtraction noise on routes this cheap. Telemetry, kept unsampled by decision, is now
the largest single share of a cached delivery request. The analytics dashboard (raw-event
aggregates) was not addressed and is unchanged.

Not done, deliberately:

- **Trace sampling (9)** — kept at everything (decision: every trace id must resolve).
- **Audit member-branch LATERAL** — measured 1.3 ms warm (the 34 ms was a cold cache); its
  `ponytail:` ceiling still holds.
- **Analytics ingest batching** — batching messages per transaction changes the consumer's
  ack/nak semantics; its per-beacon overhead shrinks with 6, 8 and 10 instead.

## Reproduce

```bash
. ~/.config/dcms-loadtest.env
node loadtest/seed/provision.mjs --env vps1 -e seed.modeB=true && node loadtest/seed/dynamic-apps.mjs --env vps1
node loadtest/seed/exempt.mjs --env vps1 add 51.195.116.123 "routecost"
./loadtest/run.sh --env vps1 --scenario routecost --via vps1 -E RC_RATE=40 -E RC_SEG=60 -E RC_GAP=15
# then: remove the exemption, run ratelimit, tear down -- README "The rate limiters, and exempting the generator"
```

`RC_ONLY=da_list,da_get_expand` re-measures a subset after a fix; compare `routecost.txt` row by row.
