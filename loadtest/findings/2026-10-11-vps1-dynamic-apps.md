# Dynamic Apps stress campaign — 2026-10-11, vps1

The first load test of the Dynamic Apps plugin. One tenant, **`da-stress`** (kept for inspection),
holds an application that uses every part of the plugin, filled with ~128k records, and was then
driven four ways: a functional probe of every feature, a probe of every hard limit, automation
storms, and a k6 staircase mixing the admin plane with the site API.

Target `https://admin.highgeek.eu` (vps1: 4 cores, 7.6 GB), deployed image `admin-api@sha256:be5657e0…`
(master `c31d326`). Generator VPSM (51.195.116.123), exempted from the rate limiters for the campaign
and removed afterwards (verified: a burst then got exactly 600 × 200 and 100 × 429). Generator load
stayed ≤ 1.3, so every number is about vps1.

Tooling (this commit): `seed/dynamic-apps.mjs` + `seed/da-model.mjs` (the tenant),
`seed/dynamic-apps-probe.mjs` (functional / limits / automation / scale / model sections),
`scenarios/dynamicapps.js` (k6). See the README section "Dynamic Apps".

## Capacity conclusion — 2026-10-12

Where Dynamic Apps stands after four rounds (findings fixed, field indexes, trigram search and capped
counts), on vps1: 4 cores and 7.6 GB, shared with every other DCMS service and the observability stack.

| | first run (`c31d326`) | now (`89236e6`) |
|---|---|---|
| throughput, mixed workload | 2.7–5.9 req/s | **~35 req/s** plateau (28.5 at 5 VUs) |
| p95 at 5 / 20 / 80 VUs | 16 s / 14.8 s / — | **0.4 s / 1.7 s / 5.7 s** |
| errors at 40 VUs | 4.6 % (timeouts) | 0.21 % |
| site page under row rules, user with 2,500 grants | 15 s | 205 ms |
| automation | 100 events/s routed, 3.9 runs/s | 34.8 runs/s, bulk flows 500/500 |
| import | 448 rows/s | 1,073–1,536 rows/s |
| concurrent flows on one record | 15/50 failed, totals wrong | 0 failed, 0 wrong |

**What the load is.** A k6 VU is a closed loop with no think time, ~8.5 requests per iteration:
59 % admin plane (sorted list, filter across a lookup, get with expand, related list, search; 7 % of
all requests are writes) and 41 % site API (24 % under row rules, for users with 200–2,500 grants).
That is heavier per request than real traffic, where most site reads are anonymous and cheap.

**Operating points.** Comfortable: **~25 req/s**, with p95 ≈ 0.1–0.4 s on the site API and under
~1 s on admin queries. Saturation: **~35 req/s**. Past that, throughput stays flat and latency grows
with the queue (80 VUs: 5.7 s p95, 0.29 % errors). Nothing crashed or restarted at any step.

**In people** (assumptions, not measurements):
- **Back-office users:** about 0.3 req/s each while working (an action every ~10 s, ~3 calls each),
  so **~70–90 working at the same moment** at the comfortable point. At a usual 10–20 % concurrency,
  that is **a few hundred named staff accounts**.
- **Site visitors:** about 0.05–0.1 req/s each on data-driven pages (a page every ~30 s, 1–3 data
  calls each), so **~250–500 browsing at once** if they had the box to themselves. Mostly anonymous
  reads should go higher; that mix was not measured on its own.
- **One shared budget:** both groups share it, e.g. 40 staff + 200 visitors. Sustained, 25 req/s is
  ~90k requests an hour; with ordinary daily peaks, ~0.5–1 M requests a day.
- **Bulk work competes:** an import with its flows running halved interactive throughput
  (14.6 vs 24.4 req/s), so schedule large imports off-peak.

**Limits that hold whatever the hardware:**
- 120 public writes an hour per address per app for anonymous visitors, 1,200 for role holders;
- 600 requests a minute per IP on content-api;
- 100 tables × 200 fields per app;
- 64 field indexes per app, 128 per tenant;
- totals counted up to 5,000.

**Data size.** Measured with 128k records in one tenant (largest table 50k). Pages sorted, filtered
or searched on flagged fields are index scans (milliseconds) and stay so as tables grow. Totals stop
at 5,001 rows of work, and related lists use the GIN index on record data. Still proportional to
table size: filters on fields not flagged filterable, and cross-table filters with a broad condition
(the scenario's industry + amount ≥ X). Expect those to degrade first past ~100k rows per table;
nothing above 50k per table was measured.

**What moves the ceiling.** Postgres CPU is the limit: it averages ~2.2 of the 4 cores, and the other
services use the rest. In order of cost:
1. Postgres on its own host or with more cores. This read-heavy, CPU-bound load should scale close to
   linearly with cores.
2. Mark the fields that cross-table filters use as filterable.
3. A read replica for the site API.
4. Partition `apps.records` by tenant or app when the number of apps grows (see `RecordIndexes`).

---

## What was built (tenant `da-stress`)

| part | contents |
|---|---|
| site | Mode C site on a provisioned domain (`1628bb27….dcms.highgeek.eu`), `/portal` gated to signed-in users |
| User Authentication | 12 invited + activated realm users `test+da-{sales01..08,mgr01,mgr02,guest01,guest02}@highgeek.eu` (password in `loadtest/fixtures/da-vps1-da-stress.password`, not committed); groups Sales, Managers; role `sales` (table perms + `flow:recalc_company:run`), role `managers` (**bypass row access**) |
| Forms | instance `contact`, form `lead` → flow `form_to_lead` |
| app `crm` | 12 tables, every field type and flag, 6 choice sets, 9 relationships (m:1 restrict / setNull / cascade / required, 1:1, m:n), 3 views, 16 flows covering every trigger (row.*, relation.created, revision.published, flow.event, form.submitted, user.invited/activated, schedule, manual, flow.invoke, a self-retriggering loop) and actions (records.*, event.publish, notifications, email) |
| public access | `products` read all (hidden `cost`), `leads` create-only, `tickets` own records, `companies`/`contacts`/`deals`/`activities` **row rules** through the permission table `company_access` (1 and 2 hops) |
| data | 5,000 companies, 2,000 profiles (1:1), 20,000 contacts, 500 products, 30,000 deals, 50,000 activities, 5,000 tickets, 3,905 access grants (sales01–07: 200 companies each, **sales08: 2,500**, guest01: 5), 3,000 deal↔product links |
| app `limits` | the configuration ceiling: 100 tables × 200 fields since 2026-10-12 (80 before the cap was raised), all public, 1,000 rows of all 200 fields in `t000` |

Seeding took 7 min 22 s. Revisions 1–2 are the seed; later ones come from the probe's rollback test.

## Headline

**Correctness: 41/41 functional checks pass at full size; 13/13 hard limits answer as documented.**
Row rules isolate users exactly, bypass works, own records, hidden/read-only fields, site flows,
page gating, concurrency, drafts, rollback, every delete behaviour, forms → flows, the cascade
limit and email all behave.

**Scale and throughput do not.** Ranked:

| # | finding | measurement | where | fix |
|---|---|---|---|---|
| 1 | **Row rules are quadratic**: a user with 2,500 grants waits **15 s** for 25 deals; at 40 VUs Postgres saturates and the whole host degrades (anonymous reads 9.9 s p95) | §1 | `Data/RowRules.cs:106` | `("Data" ->> key) = id::text` instead of `"Data" @> jsonb_build_object(key, id)`: **11,870 → 132 ms** (measured) |
| 2 | **Bulk import / bulk update run flows for 20 rows only**; the rest end `terminated` | import 100 → 20 escalated; bulk 500 deals → 20 `deal_won`, 480 terminated, all chained runs terminated | `Automation/FlowRuns.cs:132` (`MaxRunsPerCorrelation = 20`, one correlation per request) | count the limit per root record (or per causation chain), not per request; docs promise "every record fires its row-created automations" |
| 3 | **Routing is capped at 100 events/s** for the whole platform; a 116k-row import left an **83,825-event backlog** and a single unrelated flow waited **1,403 s (23 min)** | §3 | `Automation/AutomationWorker.cs:32-33, 71-110` | keep claiming while the batch was full; route in parallel |
| 4 | **Execution is capped at 4 runs/s** with the host idle; 1,000 deals won → 3,000 runs took **12.7 min** | §3 | `AutomationWorker.cs:140-147, 186-196` | same: loop while the claim returned a full batch |
| 5 | **Concurrent flows writing one record fail without retry and leave data half-applied**: 15/50 runs failed, **10/10 companies hold a wrong `total_won`** | §3 | `Automation/FlowExecutor.cs:213-215` classes every `ContractConflictException` as permanent | retry the optimistic-concurrency conflict (`RecordService.cs:674-676`), keep unique violations permanent |
| 6 | The validator allows 100 × 200 fields, but **only 80 × 200 fit** the 4 MB document cap; the code comment claims the cap clears the validator | 81st batch refused, 370 chars/field | `Metadata/ChangeApplier.cs:24-31` vs `ConfigValidator.cs:16-17` | lower `MaxTables`/`MaxFieldsPerTable` to what fits, or measure the cap on the compact form |
| 7 | **Import is N+1 and planner-sensitive**: one query per lookup and per unique key per row; contacts calls ran 6–9.4 s until autoanalyze, then 2.1 s | §2 | `Data/RecordService.cs:360-363`, name lookup `:435` (unindexed `lower(Data->>k)`) | batch the existence/uniqueness checks per call; `ANALYZE` after large imports |
| 8 | **Storage bloat**: 89 % of `flow_runs` (54,531 of 61,558) are terminated noise; `outbox` (252 MB) is twice the records (125 MB) | §4 | follows from #2; envelopes carry whole records | don't write runs refused by a limit (count them); trim envelopes |
| 9 | **Public writes: 120/h per address per app, shared by signed-in users** — an office behind one NAT gets 120 site writes an hour for everyone | probe: 120 then 429; a realm user got 429 on the same address | `Endpoints/PublicEndpoints.cs:172-188` | by design for anonymous visitors; consider a separate budget for role-holding realm users |

---

## Fixed and re-measured — 2026-10-12

Every finding was fixed, deployed (pipelines 778 and 779, master `651c8ae`) and the same campaign was
run again on the same tenant. **63/63 probe checks pass** (41 functional, 13 limits, the automation
and contention checks, scale and model).

| # | fix | commit | before | after |
|---|---|---|---|---|
| 1 | row-rule hops: equality join, then `(x IN (uncorrelated set)) IS TRUE` so Postgres hashes the set once whatever the page's ORDER BY … LIMIT | `e10a342`, `651c8ae` | sales08 deals page **15,173 ms**, company search page 714 ms (EXPLAIN) | **205 ms**, **12 ms** (EXPLAIN); k6 rule-bound search p95 at 5 VUs 20 s → **179 ms** |
| 2 | each record of an import / bulk update / bulk delete is its own action for the run limits | `a4a0b4f` | 100 imported → 20 escalated; 500 bulk-won → 20 ran, 480 terminated | **100/100**; **500/500** `deal_won` + 500/500 `on_deal_closed` |
| 3, 4 | routing and execution are separate loops that go again while batches are full | `ed62921` | 100 events/s, 3.9 runs/s; 3,000 runs in 760 s with the host idle | 1,500 runs (bulk) in **40 s**; 3,000 runs (per record) in **86 s, 34.8 runs/s** |
| 5 | a step whose record another write changed reads its earlier read-only steps again and retries (up to 10, growing pause); read-modify-write passes `version` | `cd45bee`, `92fb59b` | 15/50 runs failed, 10/10 totals wrong | **0 failed, 0 wrong** (50 deals on 10 companies at once) |
| 6 | document cap 4 M → 8 M characters | `205be90` | 80 × 200 fields fit | **100 × 200** fit and publish |
| 7 | import checks lookups and unique values once per call | `9047d3b` | activities (2 name lookups) 448 rows/s, 1.9–2.8 s per 1,000 | **1,073–1,536 rows/s**, 0.65–0.93 s per 1,000 |
| 8 | (follows from 2: no more terminated noise per bulk row) | — | 89 % of runs terminated | storms leave no terminated runs |
| 9 | a role holder's writes count against their User Authentication account; role holders share 1,200/h per address (security review of `abedcbe` → `84a7bb6`) | `abedcbe`, `84a7bb6` | 120/h per address for everyone | role holder writes with the address exhausted; a plain signed-in user still shares it |
| — | `eq null` means `isNull`; `rule-writable` warns only about what a rule reads; site API answers carry each security header once | `2372405`, `7672325`, `439f09d` | | headers checked live: once each |
| new | **OpenAPI at the new ceiling OOM'd content-api** (10 MB document cached as a JSON string, 640 MB container): written compact into gzip, cached and served as those bytes | `88f1403` | 500 `OutOfMemoryException`; content-api 535 MiB | **284 KB** gzipped (4.4 MB plain), 0.2–0.3 s warm; content-api 292 MiB |

**k6 staircase on `651c8ae`** (same scenario and steps):

| VUs | req/s | errors | p95 | site anon | site own | rule-bound search | rule-bound deals | admin query |
|---|---|---|---|---|---|---|---|---|
| 5 | **24.4** (was 2.7) | 0 % | **414 ms** (was 16 s) | 90 | 81 | 179 (was 20,001) | 409 (was 24,076) | 511 |
| 10 | **27.0** (was 3.1) | 0 % (was 2.7 %) | 849 ms (was 26.7 s) | 131 | 137 | 287 | 743 | 1,096 |
| 20 | **30.4** (was 4.9) | 0 % (was 4.7 %) | 1.6 s (was 14.8 s) | 141 | 180 | 483 | 1,385 | 2,109 |
| 40 | **30.8** (was 5.9) | 0.5 % (was 4.6 %) | 3.4 s (was 27.7 s) | 240 (was 9,935) | 331 (was 12,143) | 795 | 2,776 | 4,347 |
| 80 | **30.7** | 1.35 % | 6.0 s | 298 | 374 | 1,051 | 3,221 | 7,164 |

Throughput is ~6× higher and holds flat past the knee instead of timing out. The site API stays
sub-second to 80 VUs. **What now sets the ceiling is Postgres CPU on the admin plane's list shapes**
(all at 2.4–3 of vps1's 4 cores in the 20-VU bundle): every list page runs an exact `COUNT(*)` plus a
sort over the table's jsonb rows, and the app's configured `indexes` create no Postgres indexes. Next
steps, if that matters: expression indexes for sortable/filterable fields (or for configured
indexes), and an estimated or capped count for large tables. A large import now also drains its
flows in minutes rather than hours, which means it competes for the same CPU while it does (the
5-VU step that overlapped one measured 14.6 req/s instead of 24.4).

---

## Indexes for sortable and filterable fields — 2026-10-12

Commits `081d08a`, `5c5996c`, `bedd3a7` (pipeline 781). Each sortable field gets two partial
expression indexes on `apps.records` (ascending and descending, `NULLS LAST`, then `"Id"`, as the
compiler orders), each filterable field one, scoped to tenant + instance + table. The key is the
compiler's own SQL (`RecordIndexes.KeySql`): numbers `::numeric`, text-like values `left(…, 200)`,
dates as their fixed-width ISO text. admin-api runs as `dcms_app`, so a migration-owned
`SECURITY DEFINER` function `apps.sync_record_indexes` builds and drops them; the automation worker
calls it when a published revision changes. Index DDL time is rationed platform-wide in
`apps.record_index_sync` (2 min/hour, ≤ a tenth of the time), 64 indexes per app, 128 per tenant.
Two background security reviews shaped this: scope by tenant/instance (authors choose table ids),
no `SQLERRM` in logs, and a time ration enforced by the database (a `statement_timeout` attribute on
the function does not apply to the running statement).

On vps1: **139 indexes built in 9.8 s of DDL**, 0 failures. Plans, single query, sales data:

| shape | before | after |
|---|---|---|
| deals sorted by amount, page 11 | ~280 ms (sort 30k rows) | **4.3 ms** (index scan) |
| deals filtered via company industry + amount, sorted by close date | ~280 ms | **34 ms** (bitmap scans on two field indexes) |
| activities sorted by due date | ~180 ms | **0.33 ms** |
| planning, warm | — | 1.6–2.6 ms with 139 extra indexes (10.9 ms on a cold connection) |

k6 staircase (previous build in parentheses):

| VUs | req/s | errors | p95 | admin list | rule-bound deals |
|---|---|---|---|---|---|
| 5 | 25.0 (24.4) | 0 % | 440 ms | 201 (347) | 335 (409) |
| 20 | 31.8 (30.4) | 0 % | 1.8 s | 474 (1,346) | 1,088 (1,385) |
| 40 | 32.3 (30.8) | 0.21 % (0.5 %) | 3.8 s | 1,081 (2,797) | 2,015 (2,776) |
| 80 | 31.7 (30.7) | 0.47 % (1.35 %) | 6.4 s | 3,678 (4,880) | 2,441 (3,221) |

Sorted pages are 2–3× faster under load and errors at 80 VUs fell by two-thirds, but **mixed-workload
throughput rose ~4 %**: Postgres is still the limit, now spread over shapes these indexes do not
cover. The heaviest per request: exact `COUNT(*)` over non-selective filters (amount ≥ 40,000 matches
60 % of deals, 47 ms), free-text search (`strpos` over every searchable field, unindexed), and
rule-bound site pages. Next steps if that matters: a trigram index for searchable fields, and an
estimated or capped count for large result sets.

---

## Trigram search and capped counts — 2026-10-12

Commit `89236e6` (pipeline 783). Search compiles to `lower(x) LIKE ('%' || lower(term) || '%')` with
the term's `\ % _` escaped (same matches as the old `strpos`), and each searchable text field (or the
primary field when none is) gets a `pg_trgm` GIN index through the same rationed
`apps.sync_record_indexes`. A page's `total` stops at 5,000 with `totalCapped: true` (lists, queries,
related lists, site API schema, `records.query` in flows); the Records screen shows "5,000+".

On vps1: 164 indexes (32 trigram) built, 3.5 s of DDL, 0 failures.

| check | before | after |
|---|---|---|
| contacts search (20k rows, 3 searchable fields), count | 42 ms (reads every row) | **1.1 ms** (BitmapOr of 3 trigram indexes) |
| contacts search, page | — | **2.7 ms** |
| 30k deals, total | exact 30,000 | **5,000, totalCapped true** |
| won deals, total | 3,100 | 3,100, exact |

k6 staircase: **28.5 / 31.6 / 34.3 / 35.0 / 35.7 req/s** at 5 / 10 / 20 / 40 / 80 VUs (previous build
25.0 / 29.7 / 31.8 / 32.3 / 31.7), 0 % errors to 20 VUs, 0.29 % at 80 (was 0.47 %). Postgres still
averages 2.2 cores: what is left is the scenario's lookup-filtered query (EXISTS into companies over
a non-selective amount range) and the related lists, per request now tens of milliseconds, under load
queued behind each other on four cores.

---

## 1. Row rules (finding 1)

Latency of one site-API page at full size (10 sequential calls, p50 / p90):

| caller | grants | deals page | activities page |
|---|---|---|---|
| mgr01 (bypass role) | — | 143 / 293 ms | 96 / 172 ms |
| sales01 | 200 companies | **1,783 / 2,145 ms** | 1,872 / 2,160 ms |
| sales08 | 2,500 companies | **15,173 / 16,226 ms** | 15,900 / 16,818 ms |
| anonymous products (no rules) | — | 48 / 217 ms | — |

`EXPLAIN ANALYZE` for sales08 (25 deals sorted by amount): the second hop, company → `company_access`, is a
nested-loop semi join with the containment as a join filter — **5,000 companies × 2,500 grants =
9,373,750 jsonb `@>` evaluations, 11.7 s**. `@> jsonb_build_object(k, id)` is not an equality, so the
planner cannot hash it. Rewritten as `(ra1."Data" ->> k) = ra0."Id"::text` the same query is a
hash semi join: **132 ms**. The first hop also casts the key (`ra0."Id"::text = r."Data" ->> k`), which
hides the primary key; and every page runs the rule twice, once for `COUNT(*)` and once for the rows.

### The k6 staircase (`dynamicapps`, 2-minute steps)

p95 per request shape, in ms:

| VUs | req/s | errors | site_rules | site_rules_get | admin_query | admin_list | admin_related | site_anon | site_own | admin_get |
|---|---|---|---|---|---|---|---|---|---|---|
| 5 | 2.7 | 0 % | 24,076 | 20,001 | 470 | 376 | 396 | 100 | 113 | 55 |
| 10 | 3.1 | 2.7 % | 39,745 | 37,635 | 996 | 592 | 737 | 125 | 117 | 75 |
| 20 | 4.9 | 4.7 % | 30,217 | 44,203 | 1,739 | 1,241 | 1,289 | 168 | 210 | 123 |
| 40 | 5.9 | 4.6 % | 34,714 | 58,080 | 2,486 | 1,541 | 2,057 | **9,935** | **12,143** | 132 |

Errors are k6's 60 s timeouts on rule-bound requests. vps1's load average reached **18.5 on 4 cores**
at 40 VUs. The abandoned queries did not outlive the run, and no container restarted. With rules
taken out (the bypass user, the admin plane) the same data serves in 40–330 ms, so fixing #1 is what
decides this plugin's capacity on vps1. Bundles: `runs/20261011T2031…2042*-dynamicapps-vps1`.

## 2. Bulk import (finding 7)

1,000-row calls, `?dryRun=false`, sequential:

| table | rows | lookups / unique per row | rows/s (avg) | per call (first / min / max) |
|---|---|---|---|---|
| companies | 5,000 | 0 / 1 | 386 | 1.55 / 1.55 / 3.75 s |
| company_profiles | 2,000 | 1 (by name, 1:1) | 186 | 4.9 / 4.9 / 5.8 s |
| contacts | 20,000 | 1 / 1 | 276 | 6.0 / 2.0 / 9.4 s |
| products | 500 | 0 / 1 | 657 | 0.76 s |
| deals | 30,000 | 2 / 1 | 315 | 3.3 / 2.9 / 3.6 s |
| activities | 50,000 | 2 / 0 | 448 | 2.2 / 1.9 / 2.8 s |
| tickets | 5,000 | 1 / 0 | 734 | 1.4 s |
| company_access | 3,905 | 1 / 0 | 723 | 1.2–1.4 s |
| m:n links (one POST each, 8 in flight) | 3,000 | — | 115/s | — |

Contacts calls slowed from 6.0 to 9.4 s over the first five, then ran at ~2.1 s: autoanalyze on
`apps.records` and `apps.unique_keys` ran at 19:44:10, exactly at the switch. Four parallel imports
of 1,000 rows (no lookups) took 0.77–0.93 s each. Admin single creates: 176/s at 16 in flight, 0 errors.

## 3. Automation (findings 2–5)

| test | result |
|---|---|
| import 100 urgent tickets | **20** escalated; 80 runs "Not run: one action has already set off 20 flow runs" |
| bulk-update 500 deals → won (one call, 0.73 s) | `deal_won` 20 succeeded / **480 terminated**; `on_deal_closed` 20 terminated; `activity_touch` 20 terminated |
| 1,000 PATCHes → won (8 in flight, 7.7 s) | 3,000 runs, **all succeeded**; first run after 7.9 s, all routed after 18.9 s, queue peaked at ~1,960 pending, **drained after 760 s (3.9 runs/s)** with the host idle (load ~2, admin-api 6–30 % CPU) |
| 50 deals on 10 companies → won concurrently | **15 failed** "The record changed while you were editing it"; **10/10 companies' `total_won` wrong** (e.g. 145,004 vs 267,602). The run had stamped `won_at` before failing, so the deal looks processed |
| outbox after the 116k-row seed | **83,825** unsent at the end of the seed, drained at exactly **100/s** (2,300 per 23 s) in ~14 min; load average 11–13 meanwhile (every routed event writes a run row, even a terminated one) |
| one urgent ticket created behind that backlog | escalated after **1,403 s** |

Routing and execution are both "one batch per 1-second tick": `LIMIT 100` events and `LIMIT 4` runs,
then `PeriodicTimer.WaitForNextTickAsync` even when more are waiting. The outbox is drained across
tenants, so one tenant's import delays every tenant's automations.

## 4. Limits and model size (findings 6, 8, 9)

Every documented cap answered as documented: page size 200 (201 → 400), offset 100,000, 20
predicates, depth 5, `in` 100, 3 sorts, 200 operations per change set, 1,000 import rows (all or
nothing: one bad row writes nothing), 500 bulk ids, 256 KB per record, 16 KB flow input (413), the
cascade limit (terminated with "cascaded"), and the public write throttle (120, then 429).

Model ceiling (instance `limits`):

| measure | value |
|---|---|
| tables of 200 fields that fit | **80** (16,000 fields), 81st batch: "would exceed 4,000,000 characters" |
| applying 5 tables at a time | 631 ms first → 1,850 ms last (the whole document is re-serialized per change set) |
| validate / publish at the ceiling | 167 / 174 ms |
| published document | 5.9 MB, read in 304 ms |
| tenant OpenAPI (`/api/openapi.json`) | **8.3 MB**, 1.35 s cold, 458 ms warm (was 124 KB / 112 ms with `crm` alone) |
| site `/api/limits/_model` | 2.0 MB, 400 ms |
| import 1,000 rows × 200 fields (5.9 MB body) | 3.2 s |
| list 200 wide rows (admin / site) | 396 / 340 ms p50 |

Publishing over data: making `contacts.last_name` unique over 20,000 rows took validate 588 ms,
preview 391 ms, **publish 3.6 s** (reverting 169 ms). Model v2 (a new field + edits) over the full
data published in 258 ms.

Storage after the campaign: `apps` schema 558 MB (outbox 252 MB, records 125 MB, flow_runs 122 MB,
unique_keys 24 MB); database 1.63 → 2.21 GB; vps1 disk 85 → 88 % used.

## Smaller observations

- Admin plane at full size is healthy: get 38 ms, expand 2 lookups 84 ms, search (unindexed
  `strpos`) 138 ms, related 156 ms, first page of 50k activities 180 ms, deep page (offset 49,800)
  327 ms, filter through a lookup 286 ms (p50).
- Lookup filter values cannot be null (`eq null` → 400); a flow that queries with a template that
  may be null fails. `lead_intake` needed a step condition. Worth a validator hint or `eq null` ≡ `isNull`.
- A rule with `update: true` makes its table site-writable, so every rule that reads it is
  flagged `rule-writable` (here: contacts/deals/activities read `companies`). Correct, but noisy for
  the documented "permission table" pattern.
- Site responses carry duplicate security headers (`x-frame-options`, `x-content-type-options`, …
  twice): edge and site-host both add them.
- Edge auth limit (30/min per address, not lifted by the exemption) makes signing in 12 site users
  take ~2 minutes; `lib/auth.mjs` now waits out 429s.

## Reproduce

```sh
. ~/.config/dcms-loadtest.env
node loadtest/seed/exempt.mjs --env vps1 add 51.195.116.123 "DA stress"
node loadtest/seed/dynamic-apps.mjs --env vps1                       # adopts da-stress if present
node loadtest/seed/dynamic-apps-probe.mjs --env vps1 --only functional  # also: limits automation scale model
./loadtest/stress.sh --env vps1 --scenario dynamicapps --steps "5 10 20 40" --max-p95 30000 --max-fail 10 -- --via vps1
node loadtest/seed/exempt.mjs --env vps1 remove 51.195.116.123
```

The limits section spends the address's 120 public writes for the hour; run functional first or an
hour later. Wait for the outbox to drain after seeding before running automation (finding 3).
