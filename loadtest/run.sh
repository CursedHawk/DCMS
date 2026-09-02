#!/usr/bin/env bash
# One load run, end to end: resolve the profile, run the scenario, snapshot the evidence.
#
#   ./loadtest/run.sh --env local --scenario delivery
#   ./loadtest/run.sh --env vps1 --scenario sitehost --vus 25 --duration 3m
#   ./loadtest/run.sh --env vps1 --scenario delivery --mode internal --via vps1
#
# Produces loadtest/runs/<timestamp>-<scenario>-<env>/ holding the k6 summary, the raw
# samples, and everything collect.sh gathers for exactly the window k6 was running.
#
# k6 runs in a container rather than from an installed binary so that the version is the
# same on every machine this is ever run from, including a host reached over ssh.

set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
ENVNAME="local"; SCENARIO=""; VIA=""; MODE_OVERRIDE=""
COLLECT=1; SEED=0; TEARDOWN=0
declare -a OVERRIDES=()

while [ $# -gt 0 ]; do
    case "$1" in
        --env)      ENVNAME="$2"; shift 2 ;;
        --scenario) SCENARIO="$2"; shift 2 ;;
        --mode)     MODE_OVERRIDE="$2"; shift 2 ;;
        --vus)      OVERRIDES+=(-e "load.vus=$2"); shift 2 ;;
        --duration) OVERRIDES+=(-e "load.duration=$2"); shift 2 ;;
        --via)      VIA="$2"; shift 2 ;;
        --seed)     SEED=1; shift ;;
        --teardown) TEARDOWN=1; shift ;;
        --no-collect) COLLECT=0; shift ;;
        -e|--set)   OVERRIDES+=(-e "$2"); shift 2 ;;
        -h|--help)  sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

[ -n "$SCENARIO" ] || { echo "--scenario is required (see loadtest/scenarios/)" >&2; exit 2; }
SCRIPT="$REPO/loadtest/scenarios/$SCENARIO.js"
[ -f "$SCRIPT" ] || { echo "no such scenario: $SCRIPT" >&2; exit 2; }

[ -n "$MODE_OVERRIDE" ] && OVERRIDES+=(-e "mode=$MODE_OVERRIDE")

# One implementation of profile resolution, in Node; this just consumes it.
eval "$(node "$REPO/loadtest/lib/emit-env.mjs" --env "$ENVNAME" "${OVERRIDES[@]}")" || {
    echo "could not resolve profile '$ENVNAME'" >&2; exit 2; }

# Internal mode addresses services by their compose aliases, which resolve only on the
# compose network. Saying so now beats a run that produces nothing but DNS failures.
# A profile that names an ssh host means "this is where the stack runs"; requiring --via
# as well would just be the same fact typed twice.
[ -z "$VIA" ] && [ -n "${SSH_TARGET:-}" ] && [ "$MODE" = "internal" ] && VIA="$SSH_TARGET"

if [ "$MODE" = "internal" ] && [ -z "$VIA" ]; then
    echo "mode 'internal' addresses services by compose alias (http://content-api:8080), which" >&2
    echo "only resolves on the stack's own network. Re-run with --via <ssh-host>, or use --mode edge." >&2
    exit 2
fi

RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$SCENARIO-$ENVNAME"
OUT="$REPO/loadtest/runs/$RUN_ID"
mkdir -p "$OUT"

echo "== run $RUN_ID"
echo "   profile $PROFILE  mode $MODE  vus $VUS  duration $DURATION"
echo "   output  $OUT"

if [ "$SEED" = "1" ]; then
    echo
    echo "== seeding"
    node "$REPO/loadtest/seed/provision.mjs" --env "$ENVNAME" || { echo "seeding failed" >&2; exit 1; }
fi

FIXTURES="$REPO/loadtest/fixtures/$PROFILE.json"
[ -f "$FIXTURES" ] || {
    echo "no fixtures for '$PROFILE'. Run with --seed, or: node loadtest/seed/provision.mjs --env $ENVNAME" >&2
    exit 2
}

# ---------------------------------------------------------------------------
# The run window. Recorded around k6 alone -- not around seeding, which can take minutes
# and would drag unrelated load into every query in the bundle.
#
# Padded by a scrape interval on each side because Prometheus samples every 15s: a window
# that starts exactly when k6 does can miss the sample that contains the ramp.
# ---------------------------------------------------------------------------
# ---------------------------------------------------------------------------
# Scenarios that drive the admin plane need a bearer token. Minted here rather than at
# seed time because the access-token lifetime is ten minutes: see lib/mint-token.mjs.
# ---------------------------------------------------------------------------
ACCESS_TOKEN=""
case "$SCENARIO" in
    admin|publish|media|sitebuild|mixed)
        # Reject a run that cannot finish inside one token rather than producing a bundle
        # whose second half is 401s.
        total_seconds=$(node -e '
            const s = (v) => /^(\d+)(s|m|h)$/.exec(v);
            const to = (v) => { const m = s(v); if (!m) return 0;
                return +m[1] * ({ s: 1, m: 60, h: 3600 })[m[2]]; };
            console.log(to(process.argv[1]) + to(process.argv[2]) + 10);
        ' "$DURATION" "$RAMP")
        if [ "$total_seconds" -gt 480 ]; then
            echo "Refusing: '$SCENARIO' needs a bearer token, and this run is ${total_seconds}s." >&2
            echo "Access tokens live 600s and are not refreshed mid-run. Keep ramp+duration under 8m." >&2
            rmdir "$OUT" 2>/dev/null
            exit 2
        fi
        echo
        echo "== minting access token"
        ACCESS_TOKEN="$(node "$REPO/loadtest/lib/mint-token.mjs" --env "$ENVNAME" --inspect)" || {
            echo "could not sign in -- check DCMS_LOADTEST_USER / DCMS_LOADTEST_PASSWORD" >&2
            rmdir "$OUT" 2>/dev/null; exit 1; }
        ;;
esac

START=$(( $(date -u +%s) - 30 ))

K6_ENV=(
    -e "ACCESS_TOKEN=$ACCESS_TOKEN"
    -e "PROFILE=$PROFILE" -e "MODE=$MODE"
    -e "CONTENT_BASE=$CONTENT_BASE" -e "SITEHOST_BASE=$SITEHOST_BASE" -e "ADMIN_BASE=$ADMIN_BASE"
    -e "VUS=$VUS" -e "DURATION=$DURATION" -e "RAMP=$RAMP"
    -e "ERROR_RATE=$ERROR_RATE" -e "DELIVERY_P95=$DELIVERY_P95"
    -e "SITEHOST_P95=$SITEHOST_P95" -e "ADMIN_P95=$ADMIN_P95"
)

# Docker's own -e sets the container environment; k6 populates __ENV from its OWN -e flags.
# Passing these as `docker run -e` would leave every __ENV lookup undefined and the run
# would silently use the built-in defaults, at the wrong VU count, against the wrong host.
echo
echo "== k6"
# --user: the grafana/k6 image runs as an unprivileged uid that does not own the
# bind-mounted output directory. Without this every output file fails to open, and k6
# treats that as a fatal startup error -- the run exits 255 having made zero requests,
# which looks like a scenario bug rather than a permissions one.
docker run --rm -i \
    --user "$(id -u):$(id -g)" \
    -v "$REPO:/src:ro" -v "$OUT:/out" -w /src \
    grafana/k6 run \
    "${K6_ENV[@]}" \
    --summary-export=/out/k6-summary.json \
    --out "json=/out/k6-samples.json" \
    "/src/loadtest/scenarios/$SCENARIO.js" 2>&1 | tee "$OUT/k6.log"
K6_STATUS=${PIPESTATUS[0]}

END=$(( $(date -u +%s) + 30 ))

# Raw samples are the largest thing in a bundle by an order of magnitude and are only
# read when a summary raises a question, so they are kept compressed.
[ -f "$OUT/k6-samples.json" ] && gzip -f "$OUT/k6-samples.json"

# ---------------------------------------------------------------------------
if [ "$COLLECT" = "1" ]; then
    echo
    echo "== collecting evidence for [$START, $END]"
    if [ -n "$VIA" ]; then
        # The stores publish no host port on a deployed host, so collection has to happen
        # on the box. Ship the two scripts over rather than requiring the repo be checked
        # out there: they are the only part of this harness the host needs.
        ssh "$VIA" "mkdir -p /tmp/dcms-collect"
        scp -q "$REPO/loadtest/collect/collect.sh" "$REPO/loadtest/collect/queries.sh" "$VIA:/tmp/dcms-collect/"
        # `docker compose -f ...` resolves those paths relative to the working directory,
        # so the collector has to run from the directory holding the compose files.
        # DEPLOY_PATH is intentionally NOT quoted for the remote shell: it commonly starts
        # with ~, and a quoted tilde is a literal directory name that does not exist.
        ssh "$VIA" "cd $DEPLOY_PATH && PGUSER_='$PG_USER' PGDB_='$PG_DB' \
            bash /tmp/dcms-collect/collect.sh \
            --start $START --end $END --out /tmp/dcms-collect/$RUN_ID \
            --compose '$COMPOSE_FILES'"
        COLLECT_STATUS=$?
        scp -qr "$VIA:/tmp/dcms-collect/$RUN_ID/." "$OUT/" 2>/dev/null
        ssh "$VIA" "rm -rf /tmp/dcms-collect/$RUN_ID"
    else
        ( cd "$REPO" && PGUSER_="$PG_USER" PGDB_="$PG_DB" \
            bash loadtest/collect/collect.sh --start "$START" --end "$END" --out "$OUT" \
            --compose "$COMPOSE_FILES" )
        COLLECT_STATUS=$?
    fi
else
    COLLECT_STATUS=0
fi

# ---------------------------------------------------------------------------
# The manifest is what makes a bundle readable months later: which commit, which profile,
# which window. A directory of JSON with no provenance is not evidence.
cat > "$OUT/manifest.json" <<JSON
{
  "runId": "$RUN_ID",
  "scenario": "$SCENARIO",
  "profile": "$PROFILE",
  "mode": "$MODE",
  "gitSha": "$(git -C "$REPO" rev-parse HEAD 2>/dev/null || echo unknown)",
  "gitDirty": $(if [ -n "$(git -C "$REPO" status --porcelain 2>/dev/null)" ]; then echo true; else echo false; fi),
  "startedAt": $START,
  "endedAt": $END,
  "durationSeconds": $(( END - START )),
  "load": { "vus": $VUS, "duration": "$DURATION", "ramp": "$RAMP" },
  "thresholds": {
    "errorRate": $ERROR_RATE, "deliveryP95": $DELIVERY_P95,
    "siteHostP95": $SITEHOST_P95, "adminP95": $ADMIN_P95
  },
  "targets": { "admin": "$ADMIN_BASE", "content": "$CONTENT_BASE", "siteHost": "$SITEHOST_BASE" },
  "k6ExitStatus": $K6_STATUS,
  "collectEmptyChecks": $COLLECT_STATUS,
  "collectedVia": "${VIA:-local}"
}
JSON

if [ "$TEARDOWN" = "1" ]; then
    echo
    echo "== teardown"
    node "$REPO/loadtest/seed/teardown.mjs" --env "$ENVNAME"
fi

echo
echo "== done: $OUT"
# k6 uses 99 for "a threshold failed" and other non-zero codes for "the run did not
# happen". Conflating them would report a broken harness as a performance finding.
case "$K6_STATUS" in
    0) ;;
    99) echo "   k6 exited 99: a threshold was breached. That is a result, not an error -- read the bundle." ;;
    *)  echo "   k6 exited $K6_STATUS: the RUN ITSELF failed, so the numbers above are not a measurement."
        echo "   Check $OUT/k6.log before trusting anything in this bundle." ;;
esac
[ "$COLLECT_STATUS" -ne 0 ] && echo "   $COLLECT_STATUS collector check(s) produced nothing -- read the bundle with that in mind"
exit 0
