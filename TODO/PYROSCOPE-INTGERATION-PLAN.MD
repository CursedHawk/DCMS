# Pyroscope integration — plan and progress

Continuous profiling for all ten .NET services, surfaced in Grafana next to the
existing Prometheus / Loki / Tempo signals. Listed as "later" in
[`GRAFANA AND TELEMETRY.md`](GRAFANA%20AND%20TELEMETRY.md); this is that later.

Kept here so an interrupted session can be picked up without re-deriving anything.

---

## Facts the plan rests on (verified 2026-09-24)

- Every service goes through `AddDcmsServiceDefaults("<name>")`
  (`src/Shared/Dcms.Shared.Hosting/DcmsHostingExtensions.cs`), and every name equals the
  compose service name: identity, admin-api, content-api, media-worker, email-worker,
  site-builder, site-host, edge, ai-gateway, platform-api. So profile `service_name` and
  trace `service.name` line up with no mapping.
- All ten receive `*otel-env` — directly (edge, platform-api, email-worker, site-builder)
  or through `*service-env`. A variable added to `x-otel-env` reaches every service. Do
  **not** put it in `x-service-env`: that is the trap that once left two services with no
  telemetry at all (see the comment on the anchor).
- All runtime images are `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu noble) → the **glibc**
  profiler build.
- `pyroscope/pyroscope-dotnet` on Docker Hub is stale (0.14.x). The current profiler is
  **1.5.1**, shipped as the GitHub release asset `pyroscope.1.5.1-glibc-x86_64.tar.gz`
  (grafana/pyroscope-dotnet). Supports .NET 8, 9, 10.
- Pyroscope server is **v2.3.1** (`grafana/pyroscope`). v2 still runs single-binary with
  filesystem storage (now the default), data under `./data/v2/`.
- Span profiles: NuGet `Pyroscope.OpenTelemetry` (0.5.1), `PyroscopeSpanProcessor`.
  **CPU only**; spans shorter than the ~10 ms sample interval get nothing.
- Memory is the constraint: vps1 has ~2.9 GB available, no swap, and Alloy sits at 81 %
  of its 512m. Hence services push **directly** to Pyroscope, not through Alloy.

## Decisions

- **Why the image and not "just the NuGet package":** the `Pyroscope` NuGet package is a
  14 KB managed API (labels, span-profile ids) that P/Invokes into
  `Pyroscope.Profiler.Native`; its README lists the native profiler as a prerequisite. It
  collects nothing on its own. A CLR profiler can only be loaded by the runtime at process
  start, from `CORECLR_*` environment variables — which is why those, and the `.so`, live in
  the image. So it is both layers, not either: image = the profiler, package = the optional
  trace link.
- **Non-.NET containers** (postgres, redis, nats, minio, vault, forgejo, the Node build
  sandbox) cannot take the .NET profiler at all. Their only route is Alloy `pyroscope.ebpf`,
  rejected below; they already have exporters/cAdvisor.

- **In-process CLR profiler, not Alloy `pyroscope.ebpf`.** eBPF needs a privileged Alloy
  with host PID, undoing the docker-socket-proxy isolation, and gives CPU only with weaker
  .NET symbolisation. The CLR profiler gives CPU, exceptions, lock contention, allocations.
- **Direct push** `service → pyroscope:4040`. No Alloy hop, no Alloy memory.
- **No tenant labels** on profiles — unbounded cardinality, same rule as metrics.
- **No published port, no auth** on Pyroscope. Acceptable only because Grafana reaches it
  on the compose network behind the edge's existing operator auth.
- **Allocation and heap profiling off** until overhead is measured on vps1.
- **7-day retention**, ~2 GB disk budget (Tempo's is 4 GB for the same 7 days).

---

## Phase 1 — server + Grafana (no service change) — ✅ shipped

- [x] `docker-compose.yml`: `pyroscope` service, `grafana/pyroscope:2.3.1`, `mem_limit: 384m`,
      no host port, `pyroscope-data` volume, config bind-mounted; add to grafana `depends_on`.
- [x] `infra/observability/pyroscope/pyroscope.yaml`: filesystem storage, retention.
- [x] `docker-compose.override.yml`: publish 4040 for local dev (like Tempo's 3200).
- [x] `docker-compose.vps.yml`: bind `pyroscope-data` → `~/dcms-data/pyroscope`.
- [x] `scripts/deploy.sh`: create + chown the bind-mount directory before `up` — a deploy
      step, never a runbook step.
- [x] Grafana datasource `grafana-pyroscope-datasource`, uid `dcms-pyroscope`; Tempo
      `tracesToProfiles` → it (`service.name` → `service_name`, CPU profile type).
      Profiles Drilldown ships with Grafana 12.3 — no dashboard to build.
- [x] `scripts/obs-smoke.sh`: pyroscope in the collector restart-loop check.
- [x] `scripts/deploy.sh`: pyroscope added to `CONFIG_MOUNTED_SERVICES` (its config is a
      bind-mounted file — the stale-inode trap).

Deployed to vps1 2026-09-24: `dcms-pyroscope-1` up at 18 MiB; deploy.sh created
`~/dcms-data/pyroscope` and Docker owned it 10001 — no hand step.

Verified locally (2026-09-24) before shipping:
- Config loads clean on 2.3.1 (`architecture_storage: v2`, `limits.retention_period: 7d`);
  a folded-format push to `/ingest` reads back through `QuerierService/LabelValues` —
  the call phase 4's per-service check will use.
- An empty bind-backed named volume is populated as 10001:10001 — no chown, no `user:`.
- 17 MiB idle.
- `/ready` is 503 for ~45 s after start (metastore 15 s + segment writer 30 s), then `ready`.
- Grafana 12.3.1: datasource health "Data source is working"; Tempo `tracesToProfiles`
  provisioned as written; `grafana-pyroscope-app` (Profiles Drilldown) enabled.
- Grafana has basic auth off and a `Secure` session cookie: to hit its API in a test, POST
  `/login` and send `grafana_session` as an explicit `Cookie:` header.

## Phase 2 — profiler in every image — ✅ all 10

Shape (identical in every Dockerfile — copy it from `src/Services/Dcms.EmailWorker/Dockerfile`):
- A `pyroscope` stage on the sdk image downloads the pinned 1.5.1 tarball, checks its sha256
  (`06bc3888…4b88d18`), and **strips** the native library: the release ships it with debug
  info at 181 MB, stripped it is 12.7 MB. Only the two stripped `.so` files (mode 755, so the
  non-root `$APP_UID` can load them) reach the runtime image, in `/opt/pyroscope/`.
- The runtime stage sets `CORECLR_ENABLE_PROFILING=1`, `CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`,
  `LD_PRELOAD` (ApiWrapper), `LD_LIBRARY_PATH`, `PYROSCOPE_PROFILING_ENABLED=1`,
  `PYROSCOPE_APPLICATION_NAME=<compose service name>`.
- **Kill switch, no rebuild:** `CORECLR_ENABLE_PROFILING: "0"` in that service's compose environment.

- [x] `x-otel-env`: `PYROSCOPE_SERVER_ADDRESS=http://pyroscope:4040`, exception + lock on.
      Reaches all 10 services + the 2 migrate jobs; inert for an image without the profiler.
- [x] email-worker Dockerfile (canary).
- [x] Canary confirmed on vps1 (2026-09-24, ~2 h in): healthy; Pyroscope has
      `service_name=email-worker` with `process_cpu`, `lock`, `gc` types; **84 MiB vs 71 MiB
      baseline (+13 MiB)**, CPU 0.16 % vs 0.08 % (noise). Pyroscope itself 43 MiB.
- [x] admin-api, ai-gateway, content-api, identity, media-worker, platform-api, site-builder,
      site-host. platform-api is the tightest (106 of 256 MiB at baseline, so ~120 expected).
- [x] `migrate` / `identity-migrate`: `CORECLR_ENABLE_PROFILING: "0"` — they reuse the
      admin-api / identity images and would otherwise land in those services' profiles.
- [x] The eight confirmed on vps1 (2026-09-24 13:43 UTC, release `edda61a`): all healthy,
      all nine in Pyroscope `LabelValues`. platform-api 114 of 256 MiB right after restart.
      Warm-state RSS vs baseline still to re-measure (the restart reset every process).
- [x] **edge** (sole ingress), shipped after the eight.
- The first attempt (`72eb85c`) never deployed: edge's image build hit an Ubuntu mirror
  mid-sync in its unchanged `apt-get` line; the next (`215d32e`) failed on MinIO's images
  going 401 on quay.io — fixed by mirroring them into our registry (`edda61a`).
- Watch: admin-api spawns `git` and site-builder the `docker` CLI; both inherit `LD_PRELOAD`.
  The ApiWrapper is meant to be preloaded process-wide and the docker CLI is static Go, but
  the conflict-resolve merge path in admin-api is the one to try after this deploy.
- Harmless at startup: `[Pyroscope-DotNet-Profiler-Native] [error] Error creating native
  log file` — the non-root uid cannot write the profiler's own log dir; it logs to stdout.
- Disk: after ~2 h of one service, profile data was 1.9 MB (~1 MB / 30 min); the other
  65 MB is the metastore's preallocated raft WAL segment, a fixed cost. Ten services ≈ 2–3 GB
  per 7 days before compaction — at the 2 GB budget; re-measure a day after the rollout.

Not verified locally: VPSM's disk was at 100 % (GitLab + registry live there), so no image
builds on this box — vps1 via the pipeline is the test, which is what the canary is for.

## Phase 3 — span profiles (trace → profile) — ✅

- [x] `Pyroscope.OpenTelemetry` 0.5.1 (needs OpenTelemetry 1.16.0 — exactly ours) in
      `Directory.Packages.props` + `Dcms.Shared.Hosting`; the managed `Pyroscope` package is
      pinned to **1.5.1, the same version as the native library** — the API checks native
      compatibility, and the OpenTelemetry package alone would resolve 1.3.0. Bump together.
- [x] `AddProcessor(new PyroscopeSpanProcessor())` in `AddDcmsServiceDefaults`, only when
      `CORECLR_ENABLE_PROFILING == "1"` — the same variable that is the kill switch, so turning
      the profiler off also detaches the processor.
- [x] On vps1: TraceQL `{ span.pyroscope.profile.id != nil }` returns admin-api and
      platform-api traces minutes after the rollout.
- Only traces the Alloy tail sampler keeps get a link. Fine.

## Phase 4 — verification + docs — ✅

- [x] `obs-smoke.sh`: asserts **each of the 10** `service_name`s has profiles in the last
      hour (`QuerierService/LabelValues` from the prometheus container). Also added
      platform-api to its `SERVICES` list — it had never been there, so its logs and metrics
      were unchecked too.
- Dropped: a unit test for the phase-3 gate. It is one env comparison, and the managed API
  guards itself anyway (`IsNativeInteropAvailable`).
- [x] `infra/observability/README.md`: service table, diagram, retention table (time-only cap).
- [x] ADR 0008 amendment.

---

## Risks

- **Memory.** +384m for Pyroscope, plus ~20–50 MB per profiled process: up to ~0.9 GB
  worst case against ~2.9 GB free. The email-worker trial is the gate. If hot, cut profile
  types or raise the sample interval — not limits.
- `migrate` / `identity-migrate` inherit `*service-env` and will be profiled while they
  run. Harmless.
