#!/bin/sh
# Observability smoke test. Asserts the telemetry pipeline is actually carrying data,
# not merely that the containers are up — a Grafana with no datasource, an Alloy with a
# broken pipeline and a Prometheus scraping nothing all look healthy to `docker ps`.
#
# Runs against production, where none of the stores publish a host port, so every check
# goes through `docker compose exec`. That also means it works unchanged in dev.
#
#   ./scripts/obs-smoke.sh          # from the repo root, on the host running the stack
#
# Exit status is the number of failed checks, so it is usable from a deploy script.
set -u

COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
[ -f docker-compose.vps.yml ] || COMPOSE="docker compose"

FAILURES=0
ok()   { printf '[ OK ] %s\n' "$1"; }
fail() { printf '[FAIL] %s\n' "$1"; FAILURES=$((FAILURES + 1)); }

# Everything is fetched from inside the prometheus container, over the compose network.
#
# Not from each service's own container: the Loki and Tempo images ship neither curl nor
# wget, so `exec`-ing into them to ask them how they are returns an empty string that reads
# as "down". The prometheus image has wget, is on the same network, and has to be able to
# reach all of these anyway.
fetch() { $COMPOSE exec -T prometheus wget -qO- --timeout=10 "$1" 2>/dev/null; }

SERVICES="identity admin-api content-api ai-gateway media-worker site-builder site-host email-worker"

echo "== containers"
for s in grafana prometheus loki tempo alloy; do
    state=$($COMPOSE ps --format '{{.State}}' "$s" 2>/dev/null | head -1)
    [ "$state" = "running" ] && ok "$s running" || fail "$s is '${state:-absent}'"
done

echo
echo "== prometheus targets"
# Anything not "up" is a scrape that is silently producing no data.
targets=$(fetch 'http://localhost:9090/api/v1/targets?state=any')
if [ -z "$targets" ]; then
    fail "prometheus API unreachable"
else
    down=$(printf '%s' "$targets" | grep -o '"health":"[a-z]*"' | grep -cv '"health":"up"')
    total=$(printf '%s' "$targets" | grep -c -o '"health":"[a-z]*"')
    [ "$down" -eq 0 ] && ok "all $total targets up" || fail "$down of $total targets not up"
fi

echo
echo "== metrics are arriving"
# The audit meter specifically: it was documented for a release while exporting nothing,
# and a missing metric looks exactly like an idle platform. Presence of the series is the
# check; its value is not.
# Chosen because they exist without traffic. A *counter* like dcms_audit_recorded_total has
# no series until something increments it, so an idle platform would fail a check on it —
# which is the same confusion between "not wired up" and "nothing happening" that this whole
# effort exists to remove. dcms_audit_outbox_depth is a gauge and is published on a timer, so
# its presence is a real statement that the audit meter is subscribed and exporting.
for metric in dcms_audit_outbox_depth http_server_request_duration_seconds_count \
              dotnet_gc_heap_allocated_bytes_total process_cpu_seconds_total; do
    n=$(fetch "http://localhost:9090/api/v1/query?query=count($metric)" |
        grep -o '"value":\[[^]]*\]' | head -1)
    [ -n "$n" ] && ok "$metric present" || fail "$metric has no series"
done

echo
echo "== the infrastructure exporters the dashboards read"
#
# Seventy-seven of the 224 Prometheus panels on this stack once returned nothing, and the
# causes were not application bugs: the NATS exporter namespaces its series under `gnatsd_`
# and `jetstream_` rather than `nats_`, MinIO publishes per-bucket usage on a second
# endpoint that was not scraped, Caddy 2.7 made HTTP server metrics opt-in, and PostgreSQL
# 17 moved checkpoint counters to a view postgres_exporter does not read. Every one of those
# is a scrape or a name, invisible from inside the application, and each showed up as a
# panel reading "No data" -- which is what a broken pipeline looks like too.
#
# One probe per exporter, and each is a GAUGE that exists without traffic, for the same
# reason the block above prefers dcms_audit_outbox_depth to a counter: on a quiet platform a
# counter's absence means nothing, so a check on one cannot tell "unwired" from "idle".
for probe in \
    "jetstream_consumer_num_pending|NATS JetStream per-consumer depth (nats-exporter, -jsz=all)" \
    "gnatsd_varz_connections|NATS server stats (nats-exporter, -varz)" \
    "minio_bucket_usage_total_bytes|MinIO per-bucket usage (/minio/v2/metrics/bucket scrape)" \
    "caddy_http_requests_in_flight|Caddy HTTP server metrics (the Caddyfile's servers{metrics})" \
    "pg_stat_checkpointer_num_timed|Postgres checkpoints (alloy postgres-queries.yaml)"; do
    metric=${probe%%|*}; what=${probe#*|}
    n=$(fetch "http://localhost:9090/api/v1/query?query=count($metric)" |
        grep -o '"value":\[[^]]*\]' | head -1)
    [ -n "$n" ] && ok "$what" || fail "$metric has no series — $what is not reaching Prometheus"
done

echo
echo "== every service is exporting, not just some of them"
#
# The checks above count each metric GLOBALLY, and that is precisely how two services stayed
# dark for months. site-builder and email-worker opt out of the *service-env compose anchor
# for least privilege, and the OTLP endpoint lived inside it -- so they exported nothing at
# all, while six services reporting kept every global count non-zero and this script green.
#
# So: assert per service. dotnet_process_cpu_time_seconds_total is the right probe because
# every .NET service emits it on a timer whether or not it is doing any work, so its absence
# means "not exporting" and never "idle".
for svc in $SERVICES; do
    n=$(fetch "http://localhost:9090/api/v1/query?query=count(dotnet_process_cpu_time_seconds_total%7Bservice%3D%22$svc%22%7D)" |
        grep -o '"value":\[[^]]*\]' | head -1)
    [ -n "$n" ] && ok "$svc is exporting metrics" || fail "$svc exports NO metrics (check OTEL_EXPORTER_OTLP_ENDPOINT)"
done

echo
echo "== logs"
#
# Two questions, deliberately separated, because conflating them is the mistake this whole
# stack exists to stop people making.
#
# First: is the pipeline moving *now*? Any stream with a recent entry answers that.
# Second: does Loki know each service at all? A service with no stream in a day is
# misconfigured. A service that has simply been quiet for an hour is a healthy service on an
# idle platform, and an earlier version of this script failed it — which is precisely the
# "absence of signal read as failure" confusion the observability work was meant to remove.
#
# Two label values per service, and both are correct. Logs arrive twice on purpose: over OTLP
# from Serilog, where Loki folds service.namespace and service.name into `dcms/admin-api`,
# and over the container-stdout scrape, where the relabel rule sets plain `admin-api`. The
# second path exists so the runbook's Critical AUDIT ANCHOR lines still leave the box if the
# OTLP path breaks; either one arriving means logging works.
recent=$(fetch "http://loki:3100/loki/api/v1/query_range?query=%7Bsource%3D%22docker%22%7D&limit=1&since=15m")
case "$recent" in
    *'"values":['*']'*) ok "loki is receiving (last 15 min)" ;;
    '') fail "loki unreachable" ;;
    *) fail "no log line reached loki in the last 15 minutes — the pipeline is stalled" ;;
esac

for s in $SERVICES; do
    query="%7Bservice_name%3D~%22(dcms/)%3F$s%22%7D"
    hits=$(fetch "http://loki:3100/loki/api/v1/query_range?query=$query&limit=1&since=24h")
    case "$hits" in
        *'"values":['*']'*) ok "$s known to loki" ;;
        '') fail "loki unreachable" ; break ;;
        *) fail "$s has no log lines at all in 24h" ;;
    esac
done

echo
echo "== traces"
# Tempo's own view of what it has ingested. An empty service list means the OTLP path is
# broken somewhere between the services and Tempo, which is the failure Alloy hides best.
services_json=$(fetch 'http://tempo:3200/api/search/tag/service.name/values')
count=$(printf '%s' "$services_json" | grep -o '"' | wc -l)
if [ -z "$services_json" ]; then
    fail "tempo unreachable"
elif [ "$count" -lt 4 ]; then
    fail "tempo knows no services — nothing is tracing"
else
    ok "tempo has spans from $(( (count - 2) / 2 )) service(s)"
fi

echo
echo "== grafana"
health=$(fetch 'http://grafana:3000/api/health')
case "$health" in
    *'"database": "ok"'*|*'"database":"ok"'*) ok "grafana healthy" ;;
    '') fail "grafana unreachable" ;;
    *) fail "grafana health: $health" ;;
esac

# Provisioned dashboards, counted rather than listed: the number is the thing that drifts
# when a JSON file fails to parse, and Grafana logs that failure and starts anyway.
dashboards=$(fetch 'http://grafana:3000/api/search?type=dash-db&limit=100')
found=$(printf '%s' "$dashboards" | grep -c -o '"uid"')
expected=$(find infra/observability/grafana/dashboards -name '*.json' 2>/dev/null | wc -l)
if [ "$found" -eq 0 ]; then
    # /api/search needs auth; without it this is inconclusive rather than failed.
    printf '[SKIP] dashboard count (needs an API key; expected %s files on disk)\n' "$expected"
elif [ "$found" -ge "$expected" ]; then
    ok "$found dashboards provisioned"
else
    fail "$found dashboards provisioned, $expected on disk — one failed to parse"
fi

echo
echo "== nats"
# Two collectors on purpose, and the check is that *both* are producing. nats-exporter reads
# the unauthenticated monitoring port and carries stream depth and consumer lag — the numbers
# anyone actually pages on. Surveyor collects from the $SYS account and carries per-account
# statistics and JetStream advisories, which the monitoring port does not expose. Surveyor is
# also the one that needs the config-file startup, so it is the one that silently stops
# working if NATS is ever rolled back to flag-only.
for pair in "nats-exporter:gnatsd_varz_connections" "nats-surveyor:nats_core_account_count"; do
    job=${pair%%:*}
    metric=${pair#*:}
    got=$(fetch "http://localhost:9090/api/v1/query?query=count($metric)" | grep -o '"value":\[[^]]*\]')
    [ -n "$got" ] && ok "$job producing" || fail "$job has no series ($metric absent)"
done

echo
echo "== alloy pipeline"
# Alloy reports its own components' health. A single unhealthy one is usually the whole
# reason a downstream check above failed.
alloy=$(fetch 'http://alloy:12345/api/v0/web/components')
if [ -z "$alloy" ]; then
    fail "alloy unreachable"
else
    unhealthy=$(printf '%s' "$alloy" | grep -o '"health":{"state":"[a-z]*"' | grep -cv 'healthy"')
    [ "$unhealthy" -eq 0 ] && ok "all alloy components healthy" \
        || fail "$unhealthy alloy component(s) unhealthy — open the UI on :12345"
fi

echo
echo "== retention measurement"
# That the budget is *being measured*, not whether it is being kept — the latter needs a day
# of history and has its own command, ./scripts/obs-disk-check.sh, and its own alerts. What
# this checks is the thing those all depend on: the sidecar writing, Alloy reading the
# textfile, and the series arriving. A stale timestamp here means every retention panel is
# showing a frozen number, which reads as "nothing is growing".
stores=$(fetch "http://localhost:9090/api/v1/query?query=count(dcms_store_disk_bytes)" |
         sed -n 's/.*"value":\[[0-9.]*,"\([0-9]*\)".*/\1/p')
[ "${stores:-0}" -ge 5 ] && ok "${stores} store sizes measured" \
    || fail "only ${stores:-0} store size series — is the store-usage container running?"

fresh=$(fetch "http://localhost:9090/api/v1/query?query=time()%20-%20min(dcms_store_usage_scrape_timestamp_seconds)%20%3C%20bool%201800" |
        sed -n 's/.*"value":\[[0-9.]*,"\([0-9]*\)".*/\1/p')
[ "${fresh:-0}" = "1" ] && ok "store measurements are fresh" \
    || fail "store measurements are stale — the retention panels are showing a frozen number"

echo
echo "== reporting views"
# The security boundary, not just the plumbing: dcms_grafana must read obs and must not
# reach a base table. A smoke test that only checked the first half would pass on a role
# with full access.
if $COMPOSE exec -T postgres psql -U dcms -d dcms -tAc \
     "SELECT count(*) FROM information_schema.views WHERE table_schema='obs'" >/tmp/obsviews 2>/dev/null; then
    n=$(cat /tmp/obsviews)
    [ "${n:-0}" -ge 13 ] && ok "$n obs.* views" || fail "only ${n:-0} obs.* views — the configurator did not run"
else
    fail "could not query obs.* views"
fi
if $COMPOSE exec -T postgres psql -U dcms_grafana -d dcms -tAc \
     'SELECT 1 FROM identity."AspNetUsers" LIMIT 1' >/dev/null 2>&1; then
    fail "dcms_grafana CAN read identity.AspNetUsers — the reporting role is over-privileged"
else
    ok "dcms_grafana cannot reach base tables"
fi
rm -f /tmp/obsviews

echo
if [ "$FAILURES" -eq 0 ]; then
    echo "All observability checks passed."
else
    echo "$FAILURES check(s) failed."
fi
exit "$FAILURES"
