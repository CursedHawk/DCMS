#!/usr/bin/env bash
# Snapshots everything that explains a load run's numbers, for one time window.
#
#   ./loadtest/collect/collect.sh --start <unix> --end <unix> --out <dir> \
#       --compose "-f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
#
# Runs ON the host that is running the stack: on a deployed host none of the telemetry
# stores publish a port, so every fetch goes through `docker compose exec prometheus wget`
# over the compose network -- the same route scripts/obs-smoke.sh takes, and for the same
# reason (the prometheus image has wget; the loki and tempo images ship neither wget nor
# curl, so exec-ing into them returns an empty string that reads as "down").
#
# Exit status is the number of checks that produced nothing, so a caller can refuse to
# report a clean run on an empty bundle. That matters more here than anywhere else in the
# harness: an empty prom/ file is indistinguishable from "the platform was idle", and a
# bundle you cannot tell apart from an idle platform is worthless.

set -uo pipefail   # deliberately NOT -e: one missing metric must not abandon the rest

START=""; END=""; OUT=""; COMPOSE_FILES="-f docker-compose.yml"; STEP=15
TRACE_LIMIT=15; SLOW_MS=500; MAX_TRACE_MS=30000

while [ $# -gt 0 ]; do
    case "$1" in
        --start)   START="$2"; shift 2 ;;
        --end)     END="$2"; shift 2 ;;
        --out)     OUT="$2"; shift 2 ;;
        --compose) COMPOSE_FILES="$2"; shift 2 ;;
        --step)    STEP="$2"; shift 2 ;;
        --trace-limit) TRACE_LIMIT="$2"; shift 2 ;;
        --slow-ms) SLOW_MS="$2"; shift 2 ;;
        --max-trace-ms) MAX_TRACE_MS="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

[ -n "$START" ] && [ -n "$END" ] && [ -n "$OUT" ] || {
    echo "usage: collect.sh --start <unix> --end <unix> --out <dir> [--compose '<-f ...>'] [--step 15]" >&2
    exit 2
}

HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/queries.sh"

COMPOSE="docker compose $COMPOSE_FILES"
mkdir -p "$OUT/prom" "$OUT/traces" "$OUT/logs"

EMPTY=0
ok()    { printf '[ OK ] %s\n' "$1"; }
warn()  { printf '[WARN] %s\n' "$1"; EMPTY=$((EMPTY + 1)); }

# Percent-encoding, done here rather than in the container: busybox wget's --post-data is
# not something to rely on across images, so every request is a GET with an encoded query
# string, which every wget can do.
urlencode() {
    local s="$1" out="" i c
    for (( i = 0; i < ${#s}; i++ )); do
        c="${s:i:1}"
        case "$c" in
            [a-zA-Z0-9.~_-]) out+="$c" ;;
            *) out+="$(printf '%%%02X' "'$c")" ;;
        esac
    done
    printf '%s' "$out"
}

# `< /dev/null` is load-bearing, not tidiness. `docker compose exec` reads stdin even
# with -T, and this function is called from inside a `while read` loop fed by a heredoc.
# Without it the first call swallows the remaining queries and the loop runs exactly once,
# producing a bundle with one metric in it that still looks structurally valid.
fetch() { $COMPOSE exec -T prometheus wget -qO- --timeout=30 "$1" 2>/dev/null < /dev/null; }

# ---------------------------------------------------------------------------
echo "== prometheus (${STEP}s step, $(( (END - START) )) s window)"
# ---------------------------------------------------------------------------
while IFS='|' read -r name expr; do
    [ -n "$name" ] || continue
    url="http://localhost:9090/api/v1/query_range?query=$(urlencode "$expr")&start=$START&end=$END&step=$STEP"
    body="$(fetch "$url")"
    printf '%s' "$body" > "$OUT/prom/$name.json"

    # Three distinct failures look the same from the outside and must not: no answer at
    # all, an answer that is an error, and a successful answer holding no series. Only the
    # first two are the collector's problem; the third is a fact about the run, but a
    # silent one, so it is still worth surfacing.
    case "$body" in
        '')                warn "$name: no response from prometheus" ;;
        *'"status":"error"'*) warn "$name: prometheus returned an error" ;;
        *'"result":[]'*)   warn "$name: no series in window" ;;
        *)                 ok "$name" ;;
    esac
done <<< "$QUERIES"

# ---------------------------------------------------------------------------
echo
echo "== tempo (slowest traces over ${SLOW_MS}ms)"
# ---------------------------------------------------------------------------
# The single highest-value artifact in the bundle. A latency number says an endpoint is
# slow; the span tree says which SQL statement or which MinIO call inside it owns the time,
# which is the difference between a finding and a guess.
# The upper bound is not redundant. A bare `duration > 500ms` also matches every
# long-lived SignalR connection -- /api/hub/notifications and /hub/* stay open for the
# life of a browser tab -- so the "slowest traces" came back as one twelve-minute
# WebSocket and nothing else. No HTTP request that is merely slow takes 30 seconds; one
# that does is a timeout, which is a different question asked a different way.
TRACEQL="{ duration > ${SLOW_MS}ms && duration < ${MAX_TRACE_MS}ms }"
search="$(fetch "http://tempo:3200/api/search?q=$(urlencode "$TRACEQL")&start=$START&end=$END&limit=$TRACE_LIMIT")"
printf '%s' "$search" > "$OUT/traces/_search.json"

if [ -z "$search" ]; then
    warn "tempo search returned nothing (is tempo reachable on the compose network?)"
else
    # No jq on a deployed host by assumption; grep -o over the id field is enough for a
    # flat list of trace ids and avoids adding a dependency to the one script that has to
    # run somewhere we do not control.
    ids="$(printf '%s' "$search" | grep -o '"traceID":"[0-9a-f]*"' | cut -d'"' -f4 | sort -u)"
    count=0
    for id in $ids; do
        fetch "http://tempo:3200/api/traces/$id" > "$OUT/traces/$id.json"
        [ -s "$OUT/traces/$id.json" ] && count=$((count + 1))
    done
    [ "$count" -gt 0 ] && ok "fetched $count slow traces" || warn "no traces over ${SLOW_MS}ms in window"
fi

# ---------------------------------------------------------------------------
echo
echo "== loki (warnings and errors)"
# ---------------------------------------------------------------------------
# Loki wants nanoseconds. Labels are what alloy's docker scrape sets: source/service/container.
LOGQL='{source="docker"} |~ "(?i)(error|fatal|exception|timeout|refused|denied)"'
logs="$(fetch "http://loki:3100/loki/api/v1/query_range?query=$(urlencode "$LOGQL")&start=${START}000000000&end=${END}000000000&limit=2000&direction=backward")"
printf '%s' "$logs" > "$OUT/logs/errors.json"
[ -n "$logs" ] && ok "log query returned $(printf '%s' "$logs" | grep -o '"values"' | wc -l) streams" \
                || warn "loki returned nothing"

# ---------------------------------------------------------------------------
echo
echo "== postgres (pg_stat_statements)"
# ---------------------------------------------------------------------------
# Ordered by total time rather than mean: a fast statement run a million times costs more
# than a slow one run twice, and it is the total that a load run moves. Requires
# shared_preload_libraries=pg_stat_statements, which docker-compose.prod.yml sets -- on a
# base-only local stack this correctly reports that the extension is not loaded.
PGUSER_="${PGUSER_:-dcms}"; PGDB_="${PGDB_:-dcms}"
$COMPOSE exec -T postgres psql -U "$PGUSER_" -d "$PGDB_" -At -F'|' -c "
SELECT calls,
       round(total_exec_time::numeric, 1)  AS total_ms,
       round(mean_exec_time::numeric, 2)   AS mean_ms,
       rows,
       left(regexp_replace(query, '\s+', ' ', 'g'), 300) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 40;" < /dev/null > "$OUT/pg_stat_statements.txt" 2>"$OUT/pg_stat_statements.err"

if [ -s "$OUT/pg_stat_statements.txt" ]; then
    ok "pg_stat_statements: $(wc -l < "$OUT/pg_stat_statements.txt") statements"
else
    warn "pg_stat_statements empty -- $(head -1 "$OUT/pg_stat_statements.err" 2>/dev/null || echo 'no output')"
fi

# ---------------------------------------------------------------------------
echo
echo "== host and containers"
# ---------------------------------------------------------------------------
# A point-in-time snapshot taken after the run, unlike everything above which covers the
# window. Kept because it names things Prometheus does not: restart counts, health states,
# and the actual image each container is running.
$COMPOSE ps > "$OUT/compose-ps.txt" 2>&1
docker stats --no-stream --format '{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}\t{{.NetIO}}\t{{.BlockIO}}\t{{.PIDs}}' \
    > "$OUT/docker-stats.txt" 2>&1
{ uptime; echo; free -m 2>/dev/null; echo; df -h / 2>/dev/null; } > "$OUT/host.txt" 2>&1
[ -s "$OUT/compose-ps.txt" ] && ok "compose ps" || warn "compose ps produced nothing"

echo
echo "== $EMPTY check(s) produced nothing; bundle at $OUT"
exit "$EMPTY"
