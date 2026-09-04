# ADR 0008: Observability — OpenTelemetry into a single-host LGTM stack

**Status:** accepted (2026-08-26)

## Context

DCMS ran eight .NET services, Postgres, NATS JetStream, Redis, MinIO, Vault,
Caddy and Forgejo on one 4-core, 7.6 GB, **swapless** host with no way to answer
three ordinary questions: is the platform healthy, what is this tenant doing, and
what happened to the request this user is complaining about. Operations were
`docker compose logs -f <service>`.

The groundwork was unusually close to working, and unusually broken in three
specific ways.

**OpenTelemetry was already wired and dormant.**
`DcmsHostingExtensions.AddDcmsServiceDefaults` — called by all eight services —
already built a tracer and meter provider and called `UseOtlpExporter()`, gated
on `OTEL_EXPORTER_OTLP_ENDPOINT`. That variable was set in no compose file.

**The documented audit metrics did not export.** `AuditMetrics` defines meter
`Dcms.Audit`, and `docs/runbook.md` documents its six instruments with an alert
table. A repo-wide grep for `AddMeter` returned zero hits, and the OTel SDK drops
every instrument of an unsubscribed meter. This survived a release for one
reason worth stating plainly: **the runbook's headline guidance is that
`dcms.audit.recorded` flat-lining is the alarm**, so an unsubscribed meter and a
platform under attack produce exactly the same graph. Absence of signal is only
evidence when the signal is known to be connected.

**Traces broke at every NATS hop.** `AuditPropagation` carried a custom
`Dcms-Trace-Id` header but no W3C `traceparent` and no span id, so a consumer
could label itself with the producer's trace id and could not become a child of
it. Every publish→consume path — content publish → site build, media upload →
transcode, mail → delivery — was two unrelated traces.

**And nothing surfaced a trace id to a user.** There was no `UseExceptionHandler`,
no `IExceptionHandler` and no `ProblemDetails` anywhere in `src/`. An unhandled
exception was a bare Kestrel 500: no body, no identifier, nothing to quote.

Two things also grew without bound. No compose file set a logging driver limit,
so Docker json logs were unrotated; and the `AUDIT` JetStream stream was
`--max-age 0 --discard new`, which grows forever and then starts refusing audit
publishes.

## Decision

### One collector: Grafana Alloy, not an OTel Collector plus five exporters

Alloy embeds `prometheus.exporter.unix`, `.cadvisor`, `.postgres`, `.redis` and
`.blackbox`, and `loki.source.docker`. One container therefore replaces
node-exporter, cAdvisor, postgres_exporter, redis_exporter, blackbox_exporter and
promtail. On a swapless 7.6 GB box that is not a tidiness argument: six
containers' worth of Go runtime is roughly 300 MB of resident memory that has to
come from somewhere, and when it cannot, the kernel kills something — possibly
Postgres.

Alloy reaches the Docker API through a new read-only `docker-socket-proxy-ro`
(`POST: "0"`), never the raw socket. The socket is root on the host; a log
scraper that can also create containers is a privilege escalation wearing a
dashboard.

### Prometheus, not Mimir

Single host, single tenant, 30-day retention with a hard size cap. Mimir's
object-storage architecture buys horizontal scale this deployment cannot use and
costs about a gigabyte. Prometheus runs with the remote-write receiver enabled so
Alloy pushes rather than Prometheus scraping the collector, which keeps scrape
configuration in one file.

### Metrics are low-cardinality by rule, not by hope

`DcmsMetrics` is one class, one meter (`Dcms.Platform`), registered as a
singleton, and every counter in the platform goes through it. Its header states
the constraint: a label is allowed only when its value set is bounded by
something small — a tenant count, a provider name, an outcome. **Never** a user
id, an email, an IP, a raw path, a correlation id or a resource id.

A Prometheus series exists for every distinct label combination, resident, for
the life of the retention window. One unbounded label on one hot counter is how a
metrics store runs a host out of memory, and this host has no swap to absorb it.

The questions those labels would answer are answered from Postgres (`obs.*`) or
Loki, both of which are built for high cardinality and neither of which keeps it
in memory.

### High-cardinality reporting is SQL, over views, through a role that can read nothing else

`obs.*` is thirteen read-only views over data that already exists — the audit
log, identity, tenancy, media, analytics. Grafana reads them as `dcms_grafana`:
`LOGIN NOSUPERUSER NOBYPASSRLS`, `pg_monitor`, `default_transaction_read_only`,
`USAGE` on `obs` and on nothing else.

**The control is at the database, not in Grafana.** Grafana always allows a user
with the Postgres datasource to write raw SQL; there is no setting that prevents
it. So the boundary has to be one SQL cannot cross, which is a role with no
privilege on any base table. The views are owned by `dcms` and run with owner
rights, which is what lets them report across tenants without granting the role a
single table.

`dcms_rls` is deliberately *not* reused: RLS policies key on the `app.tenant_id`
GUC, and a `dcms_rls` connection with no GUC set sees nothing at all. It is the
right role for a future per-tenant Grafana org, not for platform reporting.

**No view exposes a raw email address, password hash, token, IP address, visitor
hash or user agent.** Email appears only as a bucketed domain. That matches the
audit log's own default-deny stance on field values, and unlike a convention it
cannot be bypassed, because the role cannot reach the columns.

### Trace continuity is carried by the audit context, including across the outboxes

`AuditPropagation` gained `traceparent` and `tracestate` alongside the existing
`Dcms-*` headers. The old headers are unchanged, so a message already sitting in
a stream still restores exactly as it did.

The subtle half is the outboxes. `NatsEventPublisher` captures from
`Activity.Current`, but `OutboxDispatcher` publishes on a two-second timer where
there is no current activity — the request that enqueued the row finished minutes
ago. So `AuditScope` carries `TraceParent`/`TraceState`, stamped by
`AuditMiddleware` at request time and used as a **fallback** by `Capture`. A live
`Activity.Current` always wins, because a consumer that republishes should parent
the new message to its own span rather than to one two hops back.

That is what makes `request → cms.content_outbox → dispatcher → JetStream →
site-builder → site.published` a single trace, which is exactly the path the
audit design already documents as load-bearing.

### A user can report a trace id

- `AuditMiddleware` echoes `X-Dcms-Trace-Id` beside the existing
  `X-Dcms-Request-Id`, before the response starts, so it survives a failure.
- `DcmsExceptionHandler` returns RFC 9457 with `traceId` and `requestId`.
  **The body never contains the exception outside Development.** Exception text
  on this platform quotes connection strings and file paths, and the failing
  request is the one most likely to be someone probing. The trace id discloses
  nothing on its own and resolves to everything for someone allowed to look.
- `DcmsErrorPage` renders the same two ids as a self-contained HTML page for
  browser navigations — no stylesheet, script or font, because it has to render
  when the static pipeline or the object store is the thing that is broken.
- The admin SPA's `toastApiError` puts the id behind a copy button on every
  failed mutation.

**Honest limitation:** Tempo keeps 7 days. An id reported after that still
resolves to the audit row (400 days) and the Loki lines (30 days, 90 for
audit/security), but not to a span tree.

### Grafana is behind the edge and OIDC, with one deliberate exception

> **Superseded in part (2026-09-04).** Caddy has been replaced by `Dcms.Edge` and
> Grafana no longer speaks OIDC: the edge authenticates the operator, refuses anyone
> who is not a SuperAdmin, and asserts them with `X-WEBAUTH-USER` (Grafana
> `[auth.proxy]`). The reasoning below still holds — the gate moved earlier, not away.
> See ADR 0010 and `docs/runbook.md`.

`grafana.highgeek.eu` publishes no host port; it is reachable only through the edge.
Sign-in is OIDC against the DCMS identity service with
`role_attribute_path = contains(roles[*], 'SuperAdmin') && 'GrafanaAdmin' || 'None'`
and **`role_attribute_strict = true`**, so a user whose claims do not match is
refused rather than quietly given Viewer.

The exception is `GF_SECURITY_ADMIN_PASSWORD`, kept as a break-glass local login.
Grafana is most needed exactly when identity is the thing that is down.

For the same reason the stack does not depend on Vault. Vault on vps1 is
Shamir-sealed with no auto-unseal, so it is sealed after every reboot — the
moment the dashboards matter most. The five new secrets live in `.env`, which is
the mechanism that works when nothing else is up.

The edge's own metrics arrive over OTLP with every other .NET service's, rather
than from a scrape target. Caddy's did come from a scrape, of an internal
`:2019 { metrics }` listener and deliberately **not** its admin API — that endpoint
could rewrite the running configuration, and exposing it for a dashboard would have
traded a panel for a remote-reconfiguration primitive.

### Alerts route through the existing email queue, not a second SMTP client

The contact point is a webhook to `POST /api/internal/alerts` on admin-api, which
enqueues onto the `EMAIL` JetStream work queue that email-worker already owns.
Configuring Grafana's own SMTP would put the relay credentials in a second
container, and `docker-compose.vps.yml` is explicit that they live in exactly one.

**The webhook authenticates with a bearer token compared in constant time, not an
HMAC over the body.** This is a real downgrade and it is recorded here rather
than glossed: Grafana's webhook contact point cannot compute a body signature. A
bearer token is replayable by anyone who captures one. The compensating controls
are that the edge does route `/api/*` publicly, so this is not internal-by-network
alone — hence a Production startup guard that refuses to boot on a secret shorter
than 16 characters or drawn from a weak set, an endpoint that fails closed when
no secret is configured at all, and an audit record
(`observability.alert.notified`) for every accepted call.

### Retention is enforced in config, and three unbounded things were capped

| Store | Retention | Cap | Enforced by |
|---|---|---|---|
| Prometheus | 30 d | 8 GB | `--storage.tsdb.retention.time` + `.size` |
| Loki | 30 d (90 d audit/security) | ~8 GB | `retention_period` + `retention_stream` + compactor |
| Tempo | 7 d | ~4 GB | `block_retention: 168h` |
| Docker json logs | 3 × 50 MB per container | ~3 GB | the `x-logging` anchor |

Plus: the `AUDIT` stream gains `--max-age 720h` for fresh clusters (keeping
`--discard new`, which is deliberate — an audit publisher must see an error,
never a silent trim); and `analytics.events`, which had no retention at all,
gains a 90-day pass in `AnalyticsRetentionWorker` while `analytics.daily_rollups`
is kept forever.

Nothing here deletes an audit row. `audit.audit_events` keeps its 400-day
partition retention and its verify → anchor → drop order, untouched.

## Prepared, not built: Postgres CDC

Deferred by decision. The seam is left in a specific shape.

`wal_level=logical` is **not** set. It raises WAL volume on a disk-constrained
box for no benefit until a consumer exists, and the exact line to add
(`-c wal_level=logical -c max_replication_slots=8 -c max_wal_senders=8`, one
restart) is recorded in `infra/observability/README.md` and in a comment on the
Postgres `command:` in `docker-compose.prod.yml`.

When it lands it should be a small `Dcms.CdcWorker` on Npgsql's
`LogicalReplicationConnection` with `pgoutput` — **not** Debezium, which is a
400–600 MB JVM on a swapless 7.6 GB host and ships full row values by default.
The worker would emit schema, table, operation, tenant id, primary key and the
*names* of changed columns, never values, matching the audit log's redaction
stance.

**Why it is worth building.** Every service connects to Postgres as `dcms`, a
cluster superuser, so ADR 0007 is already blunt that the append-only trigger
stops accidents rather than attackers. CDC is the one control that sees a row
change made by direct SQL: a change with no matching `audit.audit_events` row is,
by construction, a write that bypassed the application. The metric name is
reserved — `dcms.cdc.unaudited_change` — and `audit-security/32-security.json`
carries a deliberately empty panel for it.

Until then the three transactional outboxes give a complete change stream for
application writes, and `AuditLogProjector` puts `audit.recorded` into Loki. The
gap is direct SQL only, and it is documented rather than implied.

## Consequences

- **A missing metric can no longer look like an idle platform.**
  `DcmsMeterRegistrationTests` reflects over the solution and asserts every
  `MeterName` constant is subscribed in `DcmsMeters.Dcms`. That test is worth
  more than any dashboard in this ADR: it is the thing that stops the defect that
  motivated it from recurring.
- **Meters must be added to `DcmsMeters`, not just created.** A new meter that
  nobody subscribes exports nothing, silently. The test above fails the build.
- **`WithLogging()` is deliberately not called.** Serilog is the logging
  provider and ships to OTLP through `Serilog.Sinks.OpenTelemetry`; adding the
  OTel logging provider as well would double-ship every line.
- **Redis tracing is registered conditionally.** The workers do not register an
  `IConnectionMultiplexer`, and `AddRedisInstrumentation()` resolves one from DI.
- **`AddEntityFrameworkCoreInstrumentation` is not used.** Npgsql's own
  `ActivitySource` gives the same spans at the driver level without a second span
  per command.
- **Tail sampling is present and set to keep 100 %.** At this traffic level full
  retention fits the 4 GB Tempo budget, and full retention is what makes "a user
  reports a trace id" work reliably. The error-and-slow-plus-baseline policies
  are written into `config.alloy`, commented, so throttling later is uncommenting
  a block rather than designing one.
- **PII is scrubbed in two independent places** — `SensitiveAttributeProcessor`
  in-process and `otelcol.processor.attributes` in Alloy — so a service that has
  not picked up the code-side processor still cannot ship a secret.
- **`infra/postgres/init/*` does not re-run on the provisioned vps1 cluster.**
  The `obs` views are applied by `ObservabilityViewConfigurator` from
  `TenancyMigrator` (the same pattern as `AuditSchemaConfigurator` and
  `RlsConfigurator`), so only the role and its password are manual. This is the
  same caveat the runbook already carries for the audit schema.
- **`pg_stat_statements` needs `shared_preload_libraries` and a restart.**
  `CREATE EXTENSION` alone fails.
- **NATS now starts from a config file so `nats-surveyor` has a `$SYS` account.**
  Landed through the gate in the README — baseline, store-dir backup, then the
  config proved against a throwaway server pointed at a *copy* of the store. That
  rehearsal caught two defects that would each have been an outage: unquoted
  `system_account: $SYS` is read as a variable reference, and **`$G` cannot be
  declared as an account at all**, so the tutorial shape (`accounts { APP: …,
  $SYS: … }`) would have created a new account and stranded ten streams in the old
  one. The working shape declares only `$SYS` and puts the application user in the
  top-level `authorization` block — the documented way to place a user in `$G` —
  with `no_auth_user: app`, so no stream moved and no service changed credentials.
  `prometheus-nats-exporter` still runs alongside it on the unauthenticated
  monitoring port: the overlap is deliberate, and the cheaper collector is the one
  the pageable panels are built on, so a surveyor rollback costs the per-account
  and advisory panels and nothing else.
- **The audit fan-out subject finally has a reader.** `AuditLogProjector`
  consumes `audit.recorded` and projects each chained record as a structured log
  line. It drops the actor's display name (usually an email) and the `Changes`
  array; the actor id and ref are enough to resolve who acted from the audit
  table, and a 90-day log store is queried far more casually than the audit
  schema is.
- **The retention budget is measured, not estimated.** A `store-usage` sidecar publishes
  `dcms_store_disk_bytes` per store through the node exporter's textfile collector,
  because Prometheus self-reports its TSDB size and Loki and Tempo report theirs
  nowhere at all. Every rule built on it reads `max_over_time` peaks and takes the
  trend across a day of them: these stores sawtooth (Alloy's WAL truncates,
  Prometheus compacts), and a slope through the raw series measures whichever edge
  it spans rather than any trend.
- **The node exporter runs inside Alloy, so it needs `rootfs_path`.** Without it the
  host root is reported as `/rootfs` and every rule, alert and panel written against
  `mountpoint="/"` matches nothing — silently. Three disk alerts and the whole
  resource-exhaustion dashboard were dead this way until it was set.
- **An alert that fires permanently is an alert that is off.** Two shipped that way
  and both guarded something load-bearing: `AuditLogSilentDuringTraffic` counted
  health probes as traffic, so the flagship audit-silence detector was always firing;
  and `TelemetryAgentStopped` tested `up{job="alloy"}`, which cannot exist because
  Alloy pushes rather than being scraped. Probe routes are now excluded from the RPS
  recording rules, and agent liveness reads a metric Alloy actually emits. The
  general lesson is worth keeping: an alert nobody has seen *not* fire has not been
  tested.
- **Postgres still has no automated backup on vps1.** Out of scope here, but the
  `ops/52-retention-and-disk.json` dashboard and the disk alert make its absence
  visible, and `infra/observability/README.md` records it as an open gap.
