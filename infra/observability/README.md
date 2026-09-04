# Observability — deployment and operations

Grafana, Prometheus, Loki, Tempo and a single Grafana Alloy collector, plus the
`obs.*` reporting views in Postgres. The design and the reasoning behind it are
in [`docs/adr/0008-observability.md`](../../docs/adr/0008-observability.md);
this file is what you follow to deploy it and what you read when it misbehaves.

```
 8 .NET services ──OTLP:4317──┐
 docker container stdout ─────┤
 host / cAdvisor / postgres ──┤──► Alloy ──┬──► Tempo       7 d   ~4 GB
 redis / minio / caddy ───────┤            ├──► Loki       30 d   ~8 GB
 vault / nats-exporter ───────┘            └──► Prometheus 30 d    8 GB
                                                   │
 Postgres obs.* views ──────────────────────────► Grafana ◄── Caddy ── grafana.highgeek.eu
                                                   │
                                          webhook  └──► admin-api /api/internal/alerts
                                                            └──► EMAIL queue ──► email-worker
```

---

## What runs, and what it costs

Every container carries a hard `mem_limit`. **vps1 has no swap**, so an overrun
is an OOM kill, not a slowdown — and the kernel does not necessarily kill the
container that overran.

| Service | Image | `mem_limit` | Host port |
|---|---|---|---|
| `grafana` | `grafana/grafana:12.3.1` | 256m | none — Caddy only |
| `prometheus` | `prom/prometheus:v3.7.3` | 512m | none |
| `loki` | `grafana/loki:3.6.0` | 384m | none |
| `tempo` | `grafana/tempo:2.10.0` | 384m | none |
| `alloy` | `grafana/alloy:v1.19.1` | 512m | none |
| `nats-exporter` | `natsio/prometheus-nats-exporter` | 64m | none |
| `docker-socket-proxy-ro` | `tecnativa/docker-socket-proxy` | 64m | none |
| `nats-surveyor` | `natsio/nats-surveyor:0.9.11` | 128m | none |

≈ 2.2 GB of limit against roughly 4.2 GB available. **Measured** steady state on vps1 with
the platform idle is far lower — about 800 MB across all seven — but the limits are what
protect the host, and two of them were raised after measurement: Alloy sat at 349 MiB
against a 384m limit (it is the OTLP receiver, the tail sampler, the log shipper and five
embedded exporters in one process), and the socket proxy at 29 MiB against 32m. On a
swapless box the margin above steady state *is* the safety budget.

Builds spike well above steady state, which is why the "build serially" rule matters more
after this, not less.

In development `docker-compose.override.yml` publishes Grafana on **3001** (not
3000 — Forgejo holds 3000 on vps1, and a dev port that collides with a production
port is a trap for whoever debugs through a tunnel), Prometheus on 9090, Loki on
3100, Tempo on 3200, Alloy on 12345/4317/4318 and the NATS exporter on 7777.

---

## First deployment of the observability stack

Written for vps1, which is where it was first done, and applies unchanged to any
environment: **dev and prod each run their own full LGTM stack** — separate Prometheus,
Loki, Tempo and Grafana, on separate disks — so this is a per-environment procedure, not
a one-off. Substitute the environment's own hostnames and its own secrets.

Do these in order. Steps 1–4 are prerequisites; skipping any of them produces a
container that starts and then fails in a way that looks like a config bug.

### 1. DNS

`A` record for the environment's Grafana → that host. On dev this is
`grafana.dev.highgeek.eu` → vps1; on prod, `grafana.highgeek.eu` → the prod VPS.

It must be its own name, **not** something under `*.dcms.highgeek.eu` — that
wildcard hits the on-demand-TLS catch-all, where site-host's
`/internal/tls-allowed` will (correctly) refuse to authorise a certificate for a
hostname no tenant owns.

### 2. New keys in `~/baas-dcms/.env`

```sh
GRAFANA_ADMIN_PASSWORD=…      # break-glass local login, for when identity is down
EDGE_OIDC_CLIENT_SECRET=…     # must match Identity__Edge__Secret (same var, both containers).
                              # The edge signs operators in and passes X-WEBAUTH-USER; Grafana
                              # itself no longer speaks OIDC. Unset -> break-glass login only.
GRAFANA_DB_PASSWORD=…         # the dcms_grafana Postgres role, step 5
ALERT_WEBHOOK_SECRET=…        # bearer token for POST /api/internal/alerts
NATS_SYS_PASSWORD=…           # the $SYS account user nats-surveyor collects as
NATS_APP_PASSWORD=…           # the $G user; nothing authenticates with it (no_auth_user)

# Optional. Comma-separated. Defaults to SUPERADMIN_EMAIL, which is the person who
# would be woken up anyway.
ALERT_RECIPIENTS=ops@example.com,oncall@example.com
```

`ALERT_WEBHOOK_SECRET` is read by **two** containers — grafana, which sends it as a bearer
token, and admin-api, which compares it. They must be the same string; a mismatch rejects
every alert, and a rejected alert looks exactly like having nothing to alert about.

Generate them with `openssl rand -base64 32`. **`ALERT_WEBHOOK_SECRET` must be at
least 16 characters and not a placeholder**: admin-api refuses to start in
Production on a weak one. Leaving it unset is allowed and makes the alert
endpoint fail closed.

Vault is deliberately not used for any of these — Grafana, NATS and the rest cannot read
Vault themselves, and a stack whose job is to tell you the platform is down must not depend
on the platform being up. With Shamir the point was sharper still: Vault is sealed after
every reboot, precisely the moment the dashboards matter most. Transit auto-unseal
(`infra/vault/server/seal-transit.hcl`) removes that particular window, but not the
argument — a sealed or unreachable Vault must never be able to blind the observability
stack.

### 3. Bind-mount directories

```sh
mkdir -p ~/dcms-data/{grafana,prometheus,loki,tempo,alloy}
```

**Do not chown these to the deploy user.** They are *named volumes with `o: bind`*, not
plain bind mounts, and Docker populates an empty named volume from the image — directory
ownership included. Each store's data dir ends up owned by that image's own uid (prometheus
65534, tempo 10001, grafana 472, alloy 473), and that uid is the one that can write it.
Which is why none of these services carries a `user:` override, unlike `vault` and
`forgejo`, whose plain bind mounts Docker neither populates nor chowns. It matches
`postgres` (999), `redis` (999) and `caddy` (0), already image-uid-owned under `~/dcms-data`.

Pinning them to 1001 is what a first attempt at this deployment did, and it produced
`permission denied` on `/prometheus/queries.active` and `mkdir /var/tempo/blocks`.

Loki is the one exception: its image ships `/loki` owned by **root** while the process runs
as 10001, so the populated directory needs a correction the deploy user cannot make. Use a
throwaway root container:

```sh
docker run --rm -v /home/cursedhawk/dcms-data/loki:/x alpine:3 chown -R 10001:10001 /x
```

If any of these ever comes up in a restart loop with `permission denied` on its data path,
this is why, and the same one-liner with the right uid is the fix.

### 4. Get `infra/` onto the host

**Superseded — the pipeline does this.** `scripts/ci/deploy-remote.sh` rsyncs the compose
files, `infra/` and `scripts/` to the target on every deploy, and the services run images
pulled by digest from the registry. Nothing is built on a serving host any more, and the
host no longer needs a source tree.

Kept here because the reason it mattered has not changed: **every config file in this
stack is a bind mount.** Prometheus rules, Alloy's pipeline, the 24 dashboards, the Loki
and Tempo configs — none of them are in an image. Without `infra/` on the host the
containers start against paths that do not exist, and the error points at Docker rather
than at the missing sync. It is the single easiest thing to leave out of a deploy
artifact, which is why `deploy-remote.sh` names `infra/` explicitly rather than syncing
whatever happens to be in the working directory.

For a first provisioning before the pipeline is pointed at a new host, run
`scripts/ci/deploy-remote.sh` by hand from a checkout rather than reviving the old
`tar | ssh` line — it carries the same manifest and none of the drift.

### 5. Postgres: extension, role, restart

`infra/postgres/init/*` runs only on an empty data dir, so a cluster provisioned before a
script was added never sees it — the same caveat the runbook carried for the audit schema.

**This is now automated:** the `postgres-bootstrap` compose job re-applies those scripts
against the *running* cluster, and `scripts/deploy.sh` runs it first in the job chain on
every deploy. The steps below are what it does, kept for when you need to do one of them
by hand or to understand a failure in that job. The one thing bootstrap cannot do for you
is the Postgres restart in (a), because `shared_preload_libraries` is a startup parameter.

```sh
# a) the extension needs shared_preload_libraries, which is now on the postgres
#    command: in docker-compose.prod.yml. This is the one Postgres restart.
docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml up -d postgres

# b) then, once it is back:
docker exec -i dcms-postgres-1 psql -U dcms -d dcms <<'SQL'
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;
CREATE SCHEMA IF NOT EXISTS obs;
SQL

# c) the reporting role. Replace the password with GRAFANA_DB_PASSWORD from .env.
docker exec -i dcms-postgres-1 psql -U dcms -d dcms <<'SQL'
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dcms_grafana') THEN
    CREATE ROLE dcms_grafana LOGIN PASSWORD 'REPLACE_ME' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END $$;
GRANT pg_monitor TO dcms_grafana;
ALTER ROLE dcms_grafana SET statement_timeout = '30s';
ALTER ROLE dcms_grafana SET default_transaction_read_only = on;
GRANT USAGE ON SCHEMA obs TO dcms_grafana;
SQL
```

The **views** are not created here. `ObservabilityViewConfigurator` creates them
and re-applies their grants from `TenancyMigrator` on every admin-api start, so
they follow the code rather than a one-off script. Only the role and its password
are manual.

Verify the boundary actually holds — this is the whole security argument, so
check it rather than assuming it:

```sh
docker exec -i dcms-postgres-1 psql "postgresql://dcms_grafana:REPLACE_ME@localhost/dcms" <<'SQL'
SELECT count(*) FROM obs.v_users_summary;          -- must succeed
SELECT count(*) FROM identity."AspNetUsers";       -- must fail: permission denied
SQL
```

### 6. Caddy

`grafana.highgeek.eu` and the internal `:2019 { metrics }` listener are already in
`infra/caddy/Caddyfile`. After syncing it, **restart the container — do not
reload**. A plain reload reads a stale inode on a bind mount and silently keeps
serving the old config.

```sh
docker restart dcms-caddy-1
```

### 7. Build and start, serially

Always the full three-file invocation. A partial `-f` set has caused a production
outage on this host before.

```sh
cd ~/baas-dcms
C="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"

# The stack itself pulls rather than builds, so it can come up together.
$C up -d docker-socket-proxy-ro alloy prometheus loki tempo grafana nats-exporter

# The services carry the O1/O4 code changes. Build these ONE AT A TIME —
# building more than two at once has thrashed this box into an outage.
for s in identity admin-api content-api ai-gateway media-worker site-builder site-host email-worker admin-spa; do
  $C build "$s" || break
done
$C up -d
```

### 8. Confirm

```sh
# Alloy's UI shows every component's state; it is the fastest way to see why a
# pipeline produces nothing. Not published — tunnel to it.
ssh -L 12345:localhost:12345 vps1   # then http://localhost:12345

# Prometheus targets, from inside the network:
docker exec dcms-prometheus-1 wget -qO- localhost:9090/api/v1/targets \
  | grep -o '"health":"[a-z]*"' | sort | uniq -c
```

Then open `https://grafana.highgeek.eu`, sign in with the platform SuperAdmin,
and check **Ops → Trace lookup**: publish a piece of content, take the
`X-Dcms-Trace-Id` from the response, and confirm the trace, the log lines and the
audit rows all resolve to it. That single check exercises the whole chain.

---

## NATS: the `$SYS` account and nats-surveyor

**Done — 2026-08-26.** Recorded here because the procedure is what made it safe, and because
a rollback needs the same information.

`nats-surveyor` collects from the `$SYS` account: per-account statistics and the JetStream
advisories that say when a consumer is falling behind, none of which the unauthenticated
monitoring port exposes. Reaching `$SYS` needs a user, and a user needs a config file, which
means replacing the flag-only startup of the server that carries every queue on the platform.
The failure mode of getting that wrong is *the streams are gone* — a stream is scoped to its
account and there is no migration between accounts.

So it was rehearsed rather than attempted:

```sh
# 1. Baseline. Write the numbers down; they are what you compare against afterwards.
docker run --rm --network dcms_default natsio/nats-box:latest \
  nats --server nats://nats:4222 stream report

# 2. Back up the store dir, with the server stopped so the copy is consistent.
docker compose … stop nats
docker run --rm -v /home/cursedhawk/dcms-data/nats:/src:ro -v /home/cursedhawk:/out \
  alpine:3 tar -czf /out/nats-store-$(date +%F-%H%M).tar.gz -C /src .

# 3. A throwaway server on a COPY of the store, with the new config.
docker run --rm -v /home/cursedhawk/dcms-data/nats:/src:ro -v /tmp:/dst \
  alpine:3 sh -c "mkdir -p /dst/nats-verify && cp -a /src/. /dst/nats-verify/"
docker run -d --name nats-verify --network dcms_default -v /tmp/nats-verify:/data \
  -v ~/baas-dcms/infra/nats/nats.conf:/etc/nats/nats.conf:ro \
  -e NATS_APP_PASSWORD=… -e NATS_SYS_PASSWORD=… nats:2.11 -c /etc/nats/nats.conf

# 4. Three questions of the throwaway, in this order.
#    a) do all ten streams restore, with their consumers?
nats --server nats://nats-verify:4222 stream report
#    b) can an UNAUTHENTICATED client still publish? (this is what the eight services are)
nats --server nats://nats-verify:4222 pub tenant.verify.test hello
#    c) does the sys user actually reach the system account?
nats --server nats://nats-verify:4222 --user sys --password "$NATS_SYS_PASSWORD" server list
```

**The rehearsal caught two real defects**, either of which would have been a production
outage discovered at 4222:

1. `system_account: $SYS` **must be quoted.** NATS expands `$NAME` in a *value* position as a
   variable reference, so unquoted it fails with
   `variable reference for 'SYS' can not be found`. Account names in *key* position are not
   expanded, which is why those stay bare.
2. **`$G` cannot be declared as an account at all** — `"$G" is a Reserved Account`. Every NATS
   tutorial shows `accounts { APP: {...}, $SYS: {...} }`, and following that shape here would
   have meant creating a *new* account and leaving ten streams behind in the old one. The
   working shape declares only `$SYS`, and puts the application user in the **top-level
   `authorization` block**, which is the documented way to place a user in `$G`. So the
   streams never move.

`no_auth_user: app` then maps every unauthenticated client to that user, so **no service
changed its connection string and none gained a credential to manage**. Note what that does
*not* mean: anything that can reach 4222 still has full application access. It did before
too, and NATS publishes no host port, so this is not a regression — but do not read the
presence of a password in `nats.conf` as NATS having been locked down.

Verified after the switch: all ten streams, all thirteen consumers, message counts matching
the baseline; five requests through the public edge produced five audit rows and five new
`AUDIT` stream messages; every service reconnected within the ~20 s restart window and has
logged no NATS error since.

**Rolling back** is putting `command: ["--jetstream", "--store_dir", "/data", "--http_port",
"8222"]` back on the `nats` service and removing the `nats.conf` mount. Surveyor then has no
`$SYS` to collect from and its target goes down; `nats-exporter` is unaffected and keeps the
stream-depth and consumer-lag panels working, which is why it exists separately.

## Is the budget actually being kept?

```sh
./scripts/obs-disk-check.sh          # exit status = stores projected over budget
```

The budget is stated in bytes on disk, and until this existed nothing measured that.
Prometheus self-reports its TSDB size; **Loki and Tempo expose theirs nowhere at all**, and
the dashboards otherwise had only ingest *rates* — from which you can infer growth and cannot
answer the one question the budget exists to ask.

A 16 MB `store-usage` compose service mounts every store read-only and writes
`dcms_store_disk_bytes` and `dcms_store_disk_budget_bytes` into a file Alloy's node-exporter
textfile collector reads. A compose service rather than a host cron deliberately: a cron entry
lives outside the repo, outside compose, and outside anyone's model of the deployment, and
survives exactly until the next person rebuilds the host.

**Everything here reads peaks, not current size.** These stores sawtooth — Alloy's
remote-write WAL ramps and truncates every couple of hours, Prometheus writes blocks and then
compacts them. A slope fitted through the raw series measures whichever edge it happens to
span; on this host that read Alloy's rising edge as 0.08 GB/day and projected 2.6 GB against a
256 MiB budget, for a store that is flat. So the rules use `max_over_time` for size and take
the trend across a *day* of peaks, which spans several cycles.

Three alerts sit on the same series: over budget now, projected over budget at the end of the
retention window, and measurements gone stale. The projection alert carries a maturity guard —
it needs 24 h of samples before it will fire — because a store's initial fill from empty to
steady state, extrapolated across thirty days, is a scare rather than a projection.

`obs-smoke.sh` checks that the measurement is *running and fresh*; whether the budget is being
kept is `obs-disk-check.sh`'s question and the alerts'.

## Retention, and what it is protecting

| Store | Retention | Cap | Enforced by |
|---|---|---|---|
| Prometheus | 30 d | 8 GB | `--storage.tsdb.retention.time=30d --storage.tsdb.retention.size=8GB` |
| Loki | 30 d; **90 d** for audit/security streams | ~8 GB | `retention_period` + `retention_stream` + compactor with `retention_enabled` |
| Tempo | 7 d | ~4 GB | `block_retention: 168h` |
| Grafana SQLite | — | ~100 MB | — |
| Docker json logs | 3 × 50 MB per container | ~3 GB worst case | the `x-logging` anchor in `docker-compose.yml` |

≈ 20 GB of the 40 GB free. Check it after 48 hours and extrapolate before
declaring the settings correct:

```sh
du -sh ~/dcms-data/{prometheus,loki,tempo,grafana}
```

Three things that grew without bound and no longer do:

- **Docker json logs.** No compose file set a driver limit; logs were unrotated
  platform-wide. Now capped for every service, not only the new ones.
- **The `AUDIT` JetStream stream** was `--max-age 0 --discard new` — never
  expires, then starts refusing audit publishes. `provision-streams.sh` now
  creates it with `720h`. **An existing cluster is not changed by that script**;
  `nats stream edit AUDIT --max-age=720h` is an operator decision, and
  `--discard new` must stay (an audit publisher has to see an error, never a
  silent trim).
- **`analytics.events`** had no retention at all. `AnalyticsRetentionWorker`
  prunes past `Analytics:RetentionDays` (90) in 10 000-row batches;
  `analytics.daily_rollups` is kept forever.

**Nothing here touches an audit row.** `audit.audit_events` keeps its 400-day
partition retention and its verify → anchor → drop order.

**Open gap, recorded rather than implied:** vps1 has no automated Postgres backup
and no `pg_dump` cron. Building one was out of scope here. The
`ops/52-retention-and-disk.json` dashboard and the disk alert make its absence
visible, which is not the same as fixing it.

---

## Dashboards

Provisioned from `grafana/dashboards/**` with `foldersFromFilesStructure: true`,
so the directory tree *is* the folder tree. 24 dashboards, 277 panels. Editing
one in the UI is not persistent — change the JSON and redeploy.

| Folder | Dashboards |
|---|---|
| **platform** | overview (golden signals), service detail, http traffic (RPS by second/minute/hour/day), .NET runtime |
| **infrastructure** | host (CPU, memory, **disk with fill-rate projection**, inodes, fd), containers (per-container usage vs limit, restarts, **OOM kills**), postgres, redis, storage+edge+vault, nats-jetstream |
| **application** | content pipeline, media processing, site builds, email + AI + engagement |
| **audit-security** | audit health (the six meters against the runbook's alert table), audit activity, security |
| **business** | platform usage, tenant explorer, user demographics, visitor analytics |
| **ops** | **trace lookup**, SLO, retention and disk |

**Ops → Trace lookup** is the one that makes "users can report trace ids" a real
workflow: paste an id and get the Tempo trace, the Loki lines and the matching
`audit.audit_events` rows side by side. It works because the audit table has
stored `TraceId` since ADR 0007.

---

## Troubleshooting

**No metrics from a service.** Check `OTEL_EXPORTER_OTLP_ENDPOINT` is set on it
(`x-service-env` in `docker-compose.yml`). The OTel wiring is gated on that
variable and is silent when it is unset — which is correct for tests and
`dotnet run`, and is why it went a whole release exporting nothing.

**A `Dcms.*` metric exists in code and not in Prometheus.** The meter is not in
`DcmsMeters.Dcms`. An unsubscribed meter is dropped by the SDK with no error.
`DcmsMeterRegistrationTests` fails the build for this, so it should not reach a
deploy — but if you are looking at a running system, that is the cause.

**Traces stop at a NATS hop.** The producer did not have an `Activity.Current`
*and* the `AuditScope` had no `TraceParent`. The dispatcher path relies on the
latter; see `AuditPropagation.Capture`.

**A trace id resolves to nothing in Tempo.** Tempo keeps 7 days. Past that the id
still resolves to the audit row (400 days) and the Loki lines (30/90 days) — just
not to a span tree. Say so plainly to the reporter rather than treating it as a
bug.

**A service is green everywhere but users cannot reach it.** Check the `public-*` blackbox
probes rather than the per-service ones. The internal probes ask each service whether it is
up on the compose network; they stayed green for hours while `grafana.highgeek.eu` resolved
to a different server and Caddy could not obtain a certificate for it. The public probes go
out through the host's NAT and back in through Caddy, which is the only shape of check that
sees DNS drift, an edge misconfiguration or an expired certificate.

**A `/health` is Unhealthy but the container is `healthy`.** Those are different endpoints.
The container healthcheck polls `/health/live`, which by design runs no checks at all;
`/health` runs them. site-builder was Unhealthy on `/health` for as long as it has existed
— the shared MinIO check probed `dcms-media`, which its least-privilege service account is
deliberately denied — and nothing noticed. If you add a check, look at `/health`, not at
`docker ps`.

**Grafana refuses an OIDC login.** `role_attribute_strict = true` is doing its
job: the user is not a platform SuperAdmin. That is the intended behaviour — a
Grafana Viewer here can read every tenant's usage, every audit action and every
trace. Use the break-glass local admin if you need in and identity is the problem.

**Grafana starts but the Postgres datasource errors.** The `dcms_grafana` role
does not exist yet (step 5c) or admin-api has not yet created the views. Check
`docker logs dcms-admin-api-1 | grep -i observability`.

**Every disk panel and disk alert is empty.** The node exporter runs *inside* Alloy, so
without `rootfs_path = "/rootfs"` the host root is reported as `/rootfs` and
`node_filesystem_avail_bytes{mountpoint="/"}` — which three alerts, two recording rules and
the resource-exhaustion dashboard are all written against — matches nothing. There is no error
for this; it looks exactly like a quiet, healthy host.

**An alert is firing permanently.** Treat that as a bug in the alert, not as a condition.
Two here were: `AuditLogSilentDuringTraffic` counted health probes as traffic (0.8 req/s of
compose polling `/health/live`, none of it audited by design), and `TelemetryAgentStopped`
tested `up{job="alloy"}`, which does not exist because Alloy pushes rather than being scraped.
A permanently-firing alert is a disabled alert, and both of those guard something that matters.

**Loki is not deleting anything.** `retention_period` alone is documentation; the
compactor is what enforces it. Confirm `retention_enabled: true` and that the
compactor is running.

**Loki logs `timestamp too old` in bulk right after first start.** Expected, once. The
container-log scrape reads each container's log from the beginning, and containers that have
been up for weeks have entries older than Loki's `reject_old_samples_max_age`. The position
file advances regardless, so it clears itself within a few minutes as the tail catches up.

**A config change had no effect.** Alloy's config, like Caddy's, is a bind-mounted *file*,
and a plain restart reads the old inode. `up -d --force-recreate <service>` is what actually
picks up an edited config file. This has bitten this deployment on two different services.

**Every service appears twice in a Loki label picker.** By design. Logs arrive over OTLP —
where Loki folds `service.namespace` and `service.name` into `service_name="dcms/admin-api"`
— *and* over the container-stdout scrape, where the relabel rule sets plain
`service_name="admin-api"`. The second path exists so the runbook's Critical `AUDIT ANCHOR`
lines still leave the box if the OTLP path breaks.

---

## Future: Postgres CDC

Deferred by decision, with the seam left in a known shape.

Adding it costs one line on the Postgres `command:` in
`docker-compose.prod.yml` and one restart:

```
-c wal_level=logical -c max_replication_slots=8 -c max_wal_senders=8
```

It is **not** set today: `wal_level=logical` raises WAL volume on a
disk-constrained box for no benefit until a consumer exists. The design — a small
`Dcms.CdcWorker` on Npgsql's `LogicalReplicationConnection` with `pgoutput`, not
Debezium — is in ADR 0008, along with why it is worth building: every service
connects as a cluster superuser, and CDC is the one control that sees a row
change made by direct SQL. The metric name `dcms.cdc.unaudited_change` is
reserved and `audit-security/32-security.json` already carries an empty panel for
it.
