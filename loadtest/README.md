# Load and stress testing

A harness that drives every bottleneck-bearing surface of the platform, points at an
environment by configuration, and leaves behind an evidence bundle you can read afterwards.

The platform is already well instrumented — Prometheus, Tempo, Loki, a custom
`Dcms.Platform` meter, `pg_stat_statements` — so almost nothing here is new measurement.
What was missing was *load*, a defined window to attribute it to, and one directory
holding everything that explains a number.

---

## Quick start

```sh
export DCMS_LOADTEST_USER=you@example.com DCMS_LOADTEST_PASSWORD=...   # a SuperAdmin

node loadtest/seed/provision.mjs --env vps1        # build the fixture estate
./loadtest/run.sh --env vps1 --scenario delivery --vus 10 --duration 2m
node loadtest/seed/teardown.mjs --env vps1         # remove it again
```

`run.sh` writes `loadtest/runs/<timestamp>-<scenario>-<env>/`. Neither `runs/` nor
`fixtures/` is committed.

k6 runs from the `grafana/k6` container, so there is nothing to install.

---

## Environments

One profile per environment in `env/`, selected with `--env`. Nothing in a profile is a
secret — credentials come from the environment, so profiles are shareable.

| Profile | Target | Notes |
|---|---|---|
| `local` | docker compose on your box | Correctness, not numbers: the generator shares the CPUs it is measuring. |
| `vps1` | `https://admin.dev.highgeek.eu` | The measurement target. 4 cores / 7.6 GB. |

Override anything by dotted path: `--vus 25`, `--duration 3m`, or `-e thresholds.deliveryP95=200`.

### Two modes, and why both exist

- **`edge`** (default) — through Caddy over public HTTPS. What a real client sees, TLS and
  rate limiting included.
- **`internal`** — services addressed by compose alias, from a container on the stack's own
  network. Needs `--via <ssh-host>`.

Run the same scenario in both and difference the p95: that is what separates *the edge is
slow* from *the service is slow*. Internal mode is also the only way to load the delivery
and site planes without DNS — Caddy routes `/api` on the admin host to **admin-api**, so
content delivery is reachable only through a tenant domain, and in internal mode that
domain is just a `Host` header.

---

## Scenarios

| Scenario | Drives | Auth | Safe to run with others |
|---|---|---|---|
| `delivery` | content-api reads, Redis, OpenAPI assembly, media bytes | no | yes |
| `sitehost` | site-host, MinIO, DomainResolver | no | yes |
| `admin` | admin-api list endpoints, permission cache, audit | yes | yes |
| `publish` | outbox → NATS → cache invalidation, end to end | yes | yes |
| `media` | media-worker, ImageSharp, MinIO writes | yes | **no — run alone** |
| `sitebuild` | site-builder, build queue, all three render modes | yes | **no — run alone** |
| `ratelimit` | content-api's per-IP limiter, deliberately | no | alone |
| `mixed` | all of the above at realistic ratios | yes | — |

`media` and `sitebuild` are marked because of arithmetic, not caution: the image consumer
runs four handlers, the git build lane one by default (`DCMS_BUILD_CONCURRENCY`), and each
Mode B build sandbox is allotted `DCMS_BUILD_CPUS=2` and `DCMS_BUILD_MEM=2g`. Two concurrent
Mode B builds claim the whole host.

Note that `--build-mode all` no longer measures what it originally did. Publishes are routed
by render mode onto separate subjects and site-builder drains the git-backed modes
(`site-builder-git`) separately from static bundles (`site-builder-static`), so the 95x
head-of-line blocking that run first found is gone by construction. It is now a regression
test for the split: Mode C's p95 under `all` should stay close to its p95 alone.

### `sitebuild --build-mode`

The three render modes are three different programs, and one duration threshold across them
would be meaningless for any of them. Pick one:

| `--build-mode` | Pipeline | What it costs | Needs |
|---|---|---|---|
| `StaticFiles` (default) | Mode C: unzip a staged bundle into the artifact prefix | the **floor** — queue, dispatch, MinIO writes, activation, with the build itself nearly free | nothing extra |
| `StaticPrerender` | Mode A: read committed HTML/CSS from Forgejo, assemble pages in C# | floor plus a git read plus work linear in `seed.modeAPages` | Forgejo |
| `ReactApp` | Mode B: package install + `vite build` in a per-build sandbox container | minutes; the only mode that competes for host CPU | `seed.modeB=true` when seeding, **and the sandbox image on the host** |
| `all` | round-robins over whichever modes the fixtures have | contention *between* pipelines | — |

The interesting number is not any one of these. It is Mode A minus Mode C — the assembler's
real cost, with the queue and the object writes subtracted out — and Mode B minus Mode A,
which is the whole Node toolchain.

`site_publish_to_live_ms` is queue wait **plus** work; `dcms_site_build_duration_seconds` in
the bundle is the work alone. The gap between them is the wait, and that is what a VU count
above the consumer's concurrency is for.

> **Mode B needs `dcms/site-build-sandbox:latest` present on the host.** It is the one image
> no container ever references between builds, so `docker image prune -a` deletes it and
> nothing else — which is exactly how it went missing on vps1. `scripts/deploy.sh` now fails
> the deploy if the tag does not resolve.

### The eight-minute ceiling

Identity issues access tokens with a **ten minute** lifetime
(`src/Services/Dcms.Identity/Program.cs:113`), and refresh tokens rotate, so concurrent VUs
cannot share one refresh chain without racing. `run.sh` therefore mints a token immediately
before k6 starts and **refuses an authenticated run longer than eight minutes**.

Unauthenticated scenarios (`delivery`, `sitehost`, `ratelimit`) have no such limit and are
the ones to use for a soak. If an authenticated soak is ever needed, the fix is a
single-writer token daemon holding the refresh chain — deliberately not built until
something needs it.

### The rate limiter

content-api applies a global per-IP fixed-window limiter, 600 requests / 60 s by default
(`src/Shared/Dcms.Shared.Hosting/DcmsHostingExtensions.cs:206`). A load generator is one IP,
so **any run above ~10 req/s measures the limiter, not the platform**.

`RATE_LIMIT_PERMITS` in the host's `.env` raises it for a measurement window. Raise it,
measure, **put it back**, and re-assert it:

```sh
./loadtest/run.sh --env vps1 --scenario ratelimit --vus 20 --duration 90s
```

A DoS control raised for convenience and never restored is the one nobody checks again.
That scenario exists so this one cannot happen quietly.

---

## The fixture estate

`seed/provision.mjs` builds, per tenant: a blog plugin instance, N published posts, M
uploaded images across the webp ladder's rungs, a **Mode C (StaticFiles)** site with a
provisioned domain and a succeeded build, and a **Mode A (StaticPrerender)** site in
Forgejo with a built `release` branch. A **Mode B (ReactApp)** site is added when
`seed.modeB=true`.

Four choices worth knowing:

- **Only the Mode C site gets a domain.** Every site-host and delivery measurement runs
  against it, and it is seeded by upload rather than through git so those measurements do
  not depend on the build pipeline being healthy. The git-backed sites exist to be *built*,
  which is `sitebuild`'s job, so they need no hostname.
- **The git-backed sites publish `?branch=release`.** Publishing from the default branch
  merges into release and returns no build id — the build then comes from Forgejo's push
  webhook, so a seeder taking that path would have to guess which build was its own.
  Publishing while already on release enqueues directly and returns the id to wait on.
- **Mode B's source is fetched, not written.** `GET /api/admin/sites/{id}/starter-files?flavor=starter`
  returns the scaffold the IDE seeds a new React app with — package.json, lockfile, vite
  config, and a client generated from that tenant's own OpenAPI. Mode A's source is
  generated in `seed/sites.mjs` instead, shaped like the builder's `starterFiles()` but
  deliberately independent of it: that function lives in a TypeScript app that would have to
  be built first, and a one-page starter cannot show a cost that is linear in pages.
- **Provisioned domains.** `POST /api/admin/domains/provisioned` mints a hostname under the
  platform-owned managed zone, verified on creation. The ordinary domain endpoint issues a
  TXT challenge that `Domains:AutoVerify=false` would never let a script satisfy.
- **Generated assets.** PNGs are generated, not committed, because the media pipeline
  sniffs magic bytes and re-encodes through ImageSharp — a renamed text blob is rejected at
  ingest and the scenario would measure the rejection path.

Both seeding and teardown are idempotent. Teardown finds tenants by slug prefix rather than
by reading `fixtures.json`, so it can clean up after a provisioning run that died before
writing one — which is precisely the run that leaves a mess. It refuses to run with a
prefix shorter than four characters.

---

## What a bundle contains

```
runs/<timestamp>-<scenario>-<env>/
  manifest.json            git sha (and whether the tree was dirty), profile, window, thresholds
  k6-summary.json          per-metric, per-tag statistics
  k6-samples.json.gz       every raw sample
  k6.log                   the run's console output
  prom/*.json              36 query_range results over exactly the run window
  traces/*.json            the slowest traces in the window, fetched whole
  logs/errors.json         warning and error log lines
  pg_stat_statements.txt   top 40 statements by total execution time
  compose-ps.txt, docker-stats.txt, host.txt
```

`collect.sh` exits with the number of checks that produced nothing, and `run.sh` records it
as `collectEmptyChecks`. **Check it before trusting a bundle** — an empty `prom/` file is
indistinguishable from an idle platform, and a bundle you cannot tell apart from an idle
platform is worthless.

---

## Reading a bundle

The order matters. Each step narrows what the next one has to look at.

1. **`manifest.json`** — did the run happen? `k6ExitStatus` 99 means a threshold was
   breached (a result). Anything else non-zero means the run itself failed and the numbers
   are not a measurement. `collectEmptyChecks` > 0 means part of the evidence is missing.
   `gitDirty: true` means the code measured is not any commit.

2. **`k6-summary.json`** — *which* SLO broke, and for which tag. The scenarios tag by
   request kind precisely so this step lands on an endpoint rather than on a service.

3. **`prom/latency_p95_by_route.json`** — which route owns it. The recording rules
   aggregate by service only; this is the query that names the endpoint.

4. **`prom/saturation_*.json`** — saturation or serialization? A container at its CPU quota
   *with* `saturation_cpu_throttled` rising is starved. One at 40% CPU with
   `dotnet_threadpool_queue` growing is blocked on something. These call for opposite fixes:
   more capacity versus less blocking.

5. **`traces/*.json`** — the highest-value artifact. A latency number says an endpoint is
   slow; the span tree says which SQL statement or which MinIO call inside it owns the time.
   That is the difference between a finding and a guess.

6. **`pg_stat_statements.txt`** — ordered by *total* time, not mean: a fast statement run a
   million times costs more than a slow one run twice.

7. **`prom/pipeline_*.json`** — a request path can look healthy while the queue behind it
   grows without bound. `pipeline_outbox_depth`, `pipeline_jetstream_pending` and
   `pipeline_*_duration` are where a worker bottleneck shows up.

Write the conclusion to `findings/<date>-<env>.md` — **committed**, unlike `runs/`, because
the bundle is reproducible and the reasoning is not. Each finding as `file:line`, the
measurement that proves it, and the fix. A finding without a number is a guess; a number
without a file is not actionable.

Findings so far: [`findings/2026-09-02-vps1.md`](findings/2026-09-02-vps1.md).

---

## Open hypotheses

These came out of reading the code. The harness exists to confirm or refute them, and none
should be treated as established until a bundle says so.

| # | Suspect | Where | Scenario |
|---|---|---|---|
| 1 | Hosted-site serving is uncached and buffers whole artifacts into a `byte[]`; no ETag, no 304, and a page-route miss costs up to three sequential MinIO round trips | `Dcms.SiteHost/SiteHostEndpoints.cs:19` | `sitehost` |
| 2 | The per-IP limiter dominates any single-source run; being in-process, its real ceiling is `PermitLimit × replicas` | `Dcms.Shared.Hosting/DcmsHostingExtensions.cs:206` | `ratelimit` |
| 3 | Domain resolution is a per-replica 5-minute memory cache whose `InvalidateAll` is a no-op | `Dcms.SiteHost/DomainResolver.cs:17` | `sitehost` |
| 4 | Media and site builds contend for the same four cores | `MediaConsumerBase.cs:32`, `SitePublishConsumer.cs:71`, `SandboxOptions.cs` | `media`, `sitebuild` |
| 5 | A publish bumps the instance generation counter, invalidating every cached list at once — a stampede onto Postgres under read load | `ContentApi/Delivery/ContentCacheInvalidator.cs` | `mixed`, `publish` |
| 6 | Publish throughput is floored by the outbox poller's interval | `AdminApi/Cms/OutboxDispatcher.cs` | `publish` |
| 7 | `GET /api/admin/content` is unpaged — it projects and serializes every row for an instance (**confirmed by inspection**; the cost at scale is not) | `AdminApi/Cms/ContentEndpoints.cs:29` | `admin` |
| 8 | Postgres connection saturation across 8 services × pool | — | `mixed` |

---

## First run against vps1

vps1 is a live dev deployment on four cores. Ramp into it.

1. `delivery`, 10 VUs, 2 minutes, with Grafana open. Confirm the bundle is complete.
2. Raise VUs only once that is boring.
3. `sitehost` next — hypothesis 1 is the strongest and the cheapest to confirm.
4. `media` and `sitebuild` **alone**, never beside anything else on the first pass.
5. `mixed` last. It is the one that finds contention, and the one most likely to hurt.

Afterwards: `teardown.mjs` leaves no `loadtest-*` tenant behind, `scripts/obs-smoke.sh`
still passes, and `RATE_LIMIT_PERMITS` is back where it was.
