# Grafana and telemetry — working notes

Full observability and telemetry for the DCMS stack. Design in
[`docs/adr/0008-observability.md`](../docs/adr/0008-observability.md); the deploy
procedure in [`infra/observability/README.md`](../infra/observability/README.md);
the incident-time part in `docs/runbook.md` → Observability.

Kept here so an interrupted session can be picked up without re-deriving
anything.

---

## Three defects this started from, all silent

1. **The documented audit metrics did not export.** `AuditMetrics` defines meter
   `Dcms.Audit` and `docs/runbook.md` documents its six instruments with an alert
   table. A repo-wide grep for `AddMeter` returned zero hits, and the SDK drops
   every instrument of an unsubscribed meter. It survived a release because the
   headline guidance is that `dcms.audit.recorded` *flat-lining* is the alarm —
   so an unsubscribed meter and an idle (or compromised) platform draw the same
   graph. **Absence of signal is only evidence when the signal is known to be
   connected.**
2. **Traces broke at every NATS hop.** `AuditPropagation` carried `Dcms-Trace-Id`
   but no W3C `traceparent` and no span id, so a consumer could label itself with
   the producer's trace id and could not become a child of it.
3. **Nothing surfaced a trace id to a user.** No `UseExceptionHandler`, no
   `IExceptionHandler`, no `ProblemDetails` anywhere in `src/`. An unhandled
   exception was a bare Kestrel 500 with no body.

Plus two things that grew without bound: unrotated Docker json logs, and the
`AUDIT` stream at `--max-age 0 --discard new`.

---

## ✅ O1 — Telemetry backbone (code)

**New project `src/Shared/Dcms.Shared.Telemetry`** (in `Dcms.sln`):

- `DcmsMetrics` — meter `Dcms.Platform`, singleton, the seam every subsystem adds
  a counter to. The class header states the cardinality rule: a label is allowed
  only when its value set is bounded by something small. Never a user id, email,
  IP, raw path, correlation id or resource id.
- `DcmsMeters` — the subscription list (`Dcms`, `Framework`, `All`). Exists
  because of defect 1.
- `DcmsActivitySource` — `"Dcms.Platform"`, `Start` / `StartLinked`.
- `TenantEnrichmentProcessor` — reads `AuditAmbient.Current` in **`OnEnd`**, not
  `OnStart`: the audit middleware runs before authentication, so at span start
  there is nothing to read. Stamps tenant, correlation id, actor *kind*, sandbox.
  Deliberately not actor id/ref/display.
- `SensitiveAttributeProcessor` — drops attributes matching
  `AuditRedactor.LooksSensitiveName`, strips query strings from `url.full`.

**`DcmsHostingExtensions`** (the one file all 8 services call) gained resource
attributes, Npgsql + conditional Redis tracing, `AddMeter(DcmsMeters.All)`,
explicit histogram buckets, the two processors, Serilog → OTLP, and
`AddDcmsProblemDetails()`.

**Trace continuity across processes** — `traceparent`/`tracestate` in
`AuditPropagation`, plus `AuditScope.TraceParent`/`TraceState` stamped by
`AuditMiddleware`. That second half is the non-obvious one: `OutboxDispatcher`
publishes on a two-second timer with no `Activity.Current`, so without the scope
fallback `request → outbox → JetStream → site-builder` stays two unrelated
traces. A live `Activity.Current` always wins over the fallback, so a consumer
that republishes parents to its own span.

**Trace id in the user's hand** — `X-Dcms-Trace-Id` on every response;
`DcmsExceptionHandler` (RFC 9457, no exception text outside Development);
`DcmsErrorPage` (self-contained HTML for browser navigations — no stylesheet,
script or font, because it has to render when the asset pipeline is what broke);
`toastApiError` in the admin SPA, replacing 29 inline `toast.error` ternaries
that were throwing the id away.

**Gotchas hit:**

- `TagList` lives in `System.Diagnostics`, not `.Metrics`.
- `AddNpgsql()` is an extension in namespace `Npgsql`, not the OTel one.
- `ActivityTraceId`/`ActivitySpanId` have **no `TryParse`** — only a
  `CreateFromString` that throws. Reassemble a `traceparent` and use
  `ActivityContext.TryParse`.
- A static `ActivitySource` field read for the first time *inside* an
  `ActivityListener.ShouldListenTo` callback constructs the source **during** the
  enumeration meant to attach to it, so the listener attaches to nothing. The
  class is `beforefieldinit` without a static constructor. Take a local copy
  first. This cost an hour of "StartActivity returns null".

## ✅ O2 — The stack

`infra/observability/{alloy,prometheus,loki,tempo,grafana}` + 7 new compose
services + 5 bind-mounted volumes. Alloy replaces node-exporter, cAdvisor,
postgres_exporter, redis_exporter, blackbox_exporter and promtail — one container
instead of six, which on a swapless 7.6 GB box is the whole argument. Docker API
access goes through a new read-only `docker-socket-proxy-ro` (`POST: "0"`).

`x-logging: &default-logging` (50m × 3) applied platform-wide, not just to the new
containers. **YAML anchors do not cross compose files** — the vps overlay inlines
its own `logging:` block.

Bad image tags cost a round trip: `grafana/alloy:v1.11.4` and
`natsio/nats-surveyor:0.11.0` do not exist. Verified all six against Docker Hub's
tag API.

## ✅ O3 — Infrastructure telemetry

Postgres `pg_stat_statements` + `track_io_timing` (needs
`shared_preload_libraries` and a restart — `CREATE EXTENSION` alone fails); MinIO
`MINIO_PROMETHEUS_AUTH_TYPE: public`; Vault `unauthenticated_metrics_access` on
the listener (seal state is worth alerting on given there is no auto-unseal);
`prometheus-nats-exporter` against the already-open port 8222.

Caddy metrics come from an internal `:2019 { metrics }` listener, **not** the
admin API — that endpoint can rewrite the running config, and exposing it for a
scrape would trade a dashboard for a remote-reconfiguration primitive.

`nats-surveyor` — the one item on the original list that was still gated — is now
deployed. See "NATS $SYS" below.

## ✅ O4 — Domain metrics and spans

| Where | What |
|---|---|
| `ContentEndpoints` | publish / unpublish counters, after the commit |
| `OutboxDispatcher` | depth gauge (every poll, including empty ones) + per-row lag |
| `MediaConsumerBase` | span per job, processed/bytes/duration/outcome |
| `SitePublishConsumer` | span, build count + duration by mode and outcome |
| `EmailSendConsumer` | sent / failed with a bounded reason class |
| `AccountEndpoints` | login by method and outcome, signups |
| `TenancyEndpoints` | tenants created (unlabelled — the tenant is the one value that must not be a label) |
| `PluginInstanceEndpoints` | changes, counted in `PublishChange` so a fourth kind gets counted by construction |
| `SearchDeliveryEndpoints`, `FormSubmissionEndpoints`, `ChatHub`, `ChatBotResponder` | queries, submissions, messages by sender kind |
| `Dcms.AiGateway` | tokens in/out + duration by provider and model |

`AuditMessageContext` now also counts every message it wraps —
`dcms.messages.consumed` + a handling-duration histogram, labelled by JetStream stream and
subject. Outcome is **explicit**: `RestoreAuditContext` returns a `MessageHandling` whose
`Failed(reason)` the handler calls in its catch block. Inference does not work here, and the
reason is worth remembering: a consumer that catches an exception and marks *its own* span —
which is the natural thing to do, and what the media and site-build consumers already did —
leaves the parent consumer span untouched, because a child's status does not propagate
upward. A counter reading the parent's status would have reported every handled failure as a
success.

`AuditLogProjector` finally reads `audit.recorded` — the fan-out subject the chain
writer has always published and nothing consumed. It drops the actor display name
(usually an email) and the `Changes` array; the actor id/ref is enough to resolve
who acted, and a 90-day log store is queried far more casually than the audit
schema.

**ai-gateway needed a contract change.** `IChatProvider.CompleteAsync` returned
`string`, so token counts were unreachable; it now returns
`ChatCompletionResult(Text, PromptTokens, CompletionTokens)`. Zero means "not
reported" (Ollama and LM Studio often omit usage) — the counter does not move
rather than moving by a guess.

The IDE agent's `/v1/messages` is a byte proxy with nothing to parse, and it is
the most expensive path on the platform. `AnthropicUsageScanner` reads
`input_tokens`/`output_tokens` off the stream as it passes, without buffering it
or changing a byte, taking the **maximum** seen for each — Anthropic's
`output_tokens` is cumulative across `message_delta`, so summing would report 270
for a 212-token answer. Four tests cover it, including one that feeds the stream
a byte at a time so every count is split at every possible boundary.

## ✅ O5 — SQL reporting layer

13 views in `obs`, applied idempotently by `ObservabilityViewConfigurator` from
`TenancyMigrator` (because `infra/postgres/init/*` does not re-run on the
provisioned cluster). Read by `dcms_grafana`: `NOSUPERUSER NOBYPASSRLS`,
read-only, `USAGE` on `obs` and nothing else.

**The control is at the database because Grafana always allows raw SQL.** There
is no setting that prevents an editor writing their own query, so the boundary
has to be one SQL cannot cross.

No view exposes an email, hash, token, IP, visitor hash or user agent. Email
appears only as a bucketed domain.

Validated against the live production schema: `information_schema` for exact
column names, then `EXPLAIN (COSTS OFF)` on every view body (parses and plans
without executing). 12/13 clean; the 13th "failure" was the extraction script not
substituting a C# interpolation, confirmed separately.

## ✅ O6 — Dashboards and alerts as code

24 dashboards, 277 panels, `foldersFromFilesStructure: true`. Validated
programmatically: valid JSON, unique UIDs, every panel has `gridPos` and (unless
a row or text panel) `targets`. 16 recording + 23 alerting rules, both `promtool
check`-clean.

Alerts route through a webhook to `POST /api/internal/alerts` → `EMAIL` queue.
**Grafana's webhook contact point cannot HMAC a body**, so this is a constant-time
bearer comparison, documented honestly in both the endpoint and the contact-point
YAML, with a Production startup guard against a weak secret as the compensating
control.

## ✅ O7 — Retention

Prometheus 30 d / 8 GB, Loki 30 d (90 d audit+security) / ~8 GB, Tempo 7 d / ~4
GB, Docker logs 3 × 50 MB. Plus `AUDIT` stream `--max-age 720h` for fresh
clusters (`--discard new` kept deliberately — an audit publisher must see an
error, never a silent trim) and `AnalyticsRetentionWorker` for `analytics.events`,
which had no retention at all.

`AnalyticsRetentionWorker` would have failed `AuditBulkStatementCoverageTests` on
its `ExecuteDeleteAsync`. Rather than adding a token reference to pass the test,
it does what the test is asking for: `SuppressBulkCapture()` plus one
`analytics.retention.pruned` record per pass carrying policy, cutoff and count.

---

## Verified along the way

- `promtool check rules` — 16 recording + 23 alerting rules SUCCESS; config SUCCESS.
- All 24 dashboards parse, unique UIDs, every panel has `gridPos` + `targets`.
- All 13 `obs.*` views plan against the live vps1 schema.
- All six images exist on Docker Hub at the pinned tags.
- `docker compose config -q` over the full three-file invocation.
- **The `roles` claim does reach userinfo.** `AuthorizationEndpoints.cs` returns
  `sub`, `email`, `name` and `roles`, so `contains(roles[*], 'SuperAdmin')`
  resolves. There is no `preferred_username`, so `login_attribute_path = email`.
- 80 unit + 48 plugin-SDK + 118 integration tests passing. The 4 integration failures are
  one pre-existing fixture defect (`SitePublishFixture`, `IMultiTenantContextAccessor`
  unregistered), verified against clean `master` before this work started.
- `scripts/obs-smoke.sh`, written and run against production.

## ✅ Deployed to vps1 — 2026-08-26

All eight services rebuilt (serially) and the whole stack up. Four things went wrong on the
way, all now written into `infra/observability/README.md`:

1. **Volume ownership, backwards.** The five stores use *named volumes with `o: bind`*, and
   Docker populates an empty named volume from the image — directory ownership included. So
   pinning `user: "1001:1001"` (copying the `vault`/`forgejo` precedent, which uses plain
   bind mounts) meant each process ran as a uid that did not own its own data dir:
   `permission denied` on `/prometheus/queries.active`, `mkdir /var/tempo/blocks`. Fix: drop
   the `user:` override entirely and let each image be its own uid, which matches what
   `postgres`, `redis` and `caddy` already do on this host. Loki needed one root-container
   chown because its image ships `/loki` owned by root while running as 10001.
2. **Alloy's config language has no heredoc**, and does not take comma-separated attributes
   on one line. `config = <<EOT … EOT` and `target { name = "x", address = "y" }` both fail
   to parse. The blackbox module definition is now YAML flow style in one quoted string, and
   each `target` is a multi-line block.
3. **The stale-inode trap again.** Editing Alloy's bind-mounted config and restarting the
   container reads the *old* inode — the same failure the runbook documents for Caddy.
   `up -d --force-recreate` is what picks it up.
4. **`Alerting__*` was never wired into compose.** The endpoint fails closed without a
   secret, so alerting would have been silently dead. `Alerting__Recipients` also had to
   become one comma-separated string: bound as `IList<string>` it needs
   `Alerting__Recipients__0`, which is the sort of thing that drops a recipient by one wrong
   index.

**Verified live, not assumed:**

- `scripts/obs-smoke.sh` — all checks pass.
- **2454 metric series across 25 jobs**: all eight services, blackbox probes of each,
  cadvisor, unix, postgres, redis, nats, minio, vault, caddy, alloy, grafana, loki, tempo.
- `dcms_audit_outbox_depth` and `dcms_audit_writer_lag_seconds_*` are **present in
  Prometheus** — the meter that never exported now does, on the live system.
- Tempo has spans from 6 services; Loki has all 8, on both paths.
- **The three-way join works, from the public edge.** A real request to
  `https://admin.highgeek.eu/api/admin/tenants` returned `X-Dcms-Trace-Id` on a **401** —
  the case where a user most needs something to quote — and that one string resolved to a
  Tempo trace of three spans (`GET /api/admin/tenants`, its `postgresql` child, and the
  projector's linked `audit.project`), to the `security.unauthenticated` audit row whose
  `CorrelationId` matches the response's `X-Dcms-Request-Id`, and to the Loki lines on both
  shipping paths. That is the support workflow, end to end, in production.
- 24 dashboards provisioned, alerting provisioned, no errors in Grafana's log.
- `dcms_grafana` reads all 13 `obs.*` views and is refused on every base table.
- The alert path end to end: correct bearer → `{"received":1}`, audit record, EMAIL queue,
  delivered. Wrong bearer → 401.

## ✅ Public hostname live

`grafana.highgeek.eu` had been resolving to 51.195.116.123 through the `*.highgeek.eu`
wildcard rather than to vps1, so Caddy's ACME challenge was answered by another host
entirely (`tls-alpn-01`: "no application protocol"; `http-01`: a 404). The `A` record was
added; a Caddy **restart** — not a reload — made it retry rather than wait out its backoff,
and the certificate was issued.

The OIDC chain is wired end to end: Grafana's `/login/generic_oauth` redirects to
`admin.highgeek.eu/connect/authorize?client_id=dcms-grafana&…&scope=openid profile email
roles`, and identity accepts it and sends the browser to its own login page — not
`invalid_client` or `invalid_redirect_uri`, which is what a misconfigured client returns.
The seeder logged "Seeded Grafana OIDC client" and the row is confidential with the right
redirect URI. The only step not exercised here is the human one: signing in.

### Two defects the public probes then found

**The internal probes could not have seen the DNS problem at all.** Every `/health` target
was green throughout, because each one asks a service whether it is up on the compose
network — which stayed true while the public name pointed at someone else's server. Added
`public_2xx` blackbox probes of `admin`, `grafana` and `git` over the real path: out through
the host's NAT, back in through Caddy, TLS validated. `fail_if_not_ssl` also populates
`probe_ssl_earliest_cert_expiry`, which the certificate-expiry panel had nothing to plot
without (now reading 90 / 78 / 60 days).

**site-builder's `/health` had been Unhealthy since the service was written.** The shared
`MinioHealthCheck` probes `MediaBucket` unconditionally, and site-builder connects with a
service account scoped to `dcms-sites` and `dcms-build-logs` — deliberately, so a leaked
builder key cannot touch tenant media. So the check got `AccessDenied`: a correct answer to
the wrong question. Nothing had noticed because the container healthcheck polls
`/health/live`, which runs no checks at all. Fixed with `StorageOptions.HealthBucket`
(defaults to `MediaBucket`; site-builder sets `dcms-sites`). All 11 probes now green.

This is the argument for synthetic probes in one paragraph: two real, long-standing faults,
both invisible to everything that was already reporting healthy.

## ✅ NATS $SYS account + nats-surveyor — 2026-08-26

The last named requirement, and the riskiest change in the whole effort: it replaces the
flag-only startup of the server carrying every queue on the platform, and the failure mode
is *the streams are gone* (a stream is scoped to its account; there is no migration between
accounts).

Done through the gate rather than attempted: baseline recorded, store dir backed up with the
server stopped, then the config proved against a **throwaway server pointed at a copy of the
store** before anything touched production.

**The rehearsal caught two defects, either of which was an outage:**

1. `system_account: $SYS` **must be quoted**. NATS expands `$NAME` in a *value* position as
   a variable reference — unquoted it dies with `variable reference for 'SYS' can not be
   found`. Key positions are not expanded, which is why the account names stay bare.
2. **`$G` cannot be declared as an account** — `"$G" is a Reserved Account`. Every NATS
   tutorial shows `accounts { APP: {...}, $SYS: {...} }`, and following that here would have
   created a *new* account and stranded ten streams in the old one. The working shape
   declares only `$SYS` and puts the application user in the **top-level `authorization`
   block**, which is the documented way to place a user in `$G`. Streams never move.

`no_auth_user: app` maps every unauthenticated client to that user, so no service changed
its connection string and none gained a credential. That is not the same as NATS being
locked down — anything reaching 4222 still has full application access, as it always did.

**Verified after the switch:** ten streams, thirteen consumers, message counts matching the
baseline; five requests through the public edge → five audit rows → five new `AUDIT` stream
messages; every service reconnected inside the ~20 s window and has logged no NATS error
since. Surveyor is producing 118 series (per-account JetStream stats, advisories) alongside
nats-exporter's stream-depth and consumer-lag metrics — both are now asserted by
`obs-smoke.sh`, because surveyor is the one that silently stops if NATS is ever rolled back.

## ✅ The disk / retention check — 2026-08-27

The plan said "after 48 h, `du -sh` the store directories and extrapolate to 30 days". That
is a fine check and a bad control: it depends on a person, a calendar and their arithmetic.
Built as a measurement instead.

**`scripts/obs-store-usage.sh`** runs as a 16 MB compose service with every store mounted
read-only, and writes `dcms_store_disk_bytes` + `dcms_store_disk_budget_bytes` into a file
Alloy's node-exporter textfile collector reads. A compose service rather than a host cron on
purpose — a cron entry lives outside the repo and survives until the next person rebuilds the
host. Budgets are published as their own series so a panel and a rule cannot disagree with
ADR 0008 about them.

On top of it: five recording rules, three alerts (over budget / projected over budget /
measurements stale), three dashboard panels, and **`scripts/obs-disk-check.sh`** as the
human-readable view for the 48-hour review.

**Nothing measured this before.** Prometheus self-reports its TSDB size; Loki and Tempo
expose theirs nowhere at all. The dashboards had ingest *rates*, from which you can infer
growth and cannot answer "are we inside 8/8/4 GB" — which is the only question the budget
exists to ask.

### Four defects it found on the way, three of them long-standing

1. **The host disk metrics were dead.** The node exporter runs *inside* Alloy, so the host
   root appears as `/rootfs` — and `node_filesystem_avail_bytes{mountpoint="/"}`, which
   three alerts, two recording rules and the whole resource-exhaustion dashboard are written
   against, matched nothing at all. No error anywhere: just permanently empty panels and
   alerts that could never fire, on the control that exists to stop the disk filling up.
   Fixed with `rootfs_path = "/rootfs"`. The mount exclusions were also not matching — 217
   duplicate series, one per Docker volume and per single-file bind mount, all restating
   what `/dev/sda1` already said. Now 5 real mountpoints.

2. **`AuditLogSilentDuringTraffic` was firing permanently — the flagship alert.** Its
   expression is "audit records at zero while HTTP traffic is non-zero", and on an idle
   platform *all* the traffic is health probes: compose polling `/health/live` on eight
   containers plus Caddy polling `/health`, 0.8 req/s of pure self-inspection, none of it
   audited by design. A permanently-firing alert is a disabled alert, and this is the one
   that detects the audit log going quiet — the exact failure this whole effort was started
   over. Fixed by excluding probe routes from the RPS recording rules, which is right
   anyway: nobody wants a requests-per-second graph dominated by polling.

3. **`TelemetryAgentStopped` could never mean anything.** It tested
   `up{job="alloy"} == 0 or absent(up{job="alloy"})`, but Alloy pushes by remote_write
   rather than being scraped, so Prometheus never generates an `up` series for it — the
   `absent()` branch was true forever. Now `absent_over_time(alloy_build_info[10m])`, which
   is something Alloy actually emits.

4. **`deriv` over a sawtooth measures the edge, not the trend.** Alloy's write-ahead log
   ramps to ~45 MiB and truncates back to ~23 MiB every couple of hours; Prometheus writes
   blocks then compacts them. A slope fitted through the raw series read Alloy's rising edge
   as 0.08 GB/day and projected 2.6 GB against a 256 MiB budget — for a store that is
   perfectly flat. Every rule is now built on `max_over_time` peaks, with the trend taken
   across a *day* of peaks so it spans several cycles. Both the alert and the script also
   carry a maturity guard (24h of samples, self-calibrating against the scrape interval)
   so a freshly deployed store's initial fill is not extrapolated across thirty days.

Current reading: host 39.9 GiB free / 44% used, all stores far inside budget, projections
still marked provisional until a day of history accumulates.

## ✅ Load / OTel overhead — 2026-08-27

`scripts/load-smoke.js` ramps to 50 VUs against a delivery plane the platform rate-limits to
600 req/60 s per IP — roughly 900× over, so it measures the limiter and not the service
(99.8% 429s). Re-run under the limit: **p95 = 7.0 ms**, against a 150 ms gate and a 30 ms
internal target. OTel is not moving p95.

The mismatch between that script's profile and the platform's own rate limiter is a
pre-existing repo inconsistency, left alone rather than change a documented gate unasked.

## Left to do

- [ ] Sign in to Grafana at https://grafana.highgeek.eu through OIDC (the browser step),
      and confirm `role_attribute_strict` refuses a non-SuperAdmin.
- [ ] Re-run `./scripts/obs-disk-check.sh` once a day of history exists, so the projections
      stop being provisional. Nothing to remember: the alerts carry the same guard.

## Later, deliberately not now

Postgres CDC (designed, deferred — ADR 0008; one `command:` line and one restart
when it lands). Grafana Faro RUM in the admin SPA and tenant sites. Pyroscope
profiling. Per-tenant Grafana orgs using `dcms_rls` with `app.tenant_id` set per
datasource — the `obs.*` views are shaped so this is additive. Automated Postgres
backups, which vps1 still does not have.
