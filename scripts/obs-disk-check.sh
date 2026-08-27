#!/bin/sh
# The retention budget check, as a command rather than as something someone remembers to do.
#
# The plan said "after 48 h, du -sh the store directories and extrapolate to 30 days". That is
# a fine check and a bad control: it depends on a person, a calendar, and their arithmetic. The
# measurement itself now runs continuously — the store-usage sidecar publishes
# dcms_store_disk_bytes and three alerts fire on it — and this is the human-readable view of
# the same series, for the 48-hour review and for any time someone asks "are we inside budget".
#
#   ./scripts/obs-disk-check.sh
#
# Exit status is the number of stores projected over budget, so it is usable from a script.
set -u

COMPOSE="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
[ -f docker-compose.vps.yml ] || COMPOSE="docker compose"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# Encode just enough of PromQL for a query string.
enc() {
    printf '%s' "$1" | sed -e 's/%/%25/g' -e 's/ /%20/g' -e 's/(/%28/g' -e 's/)/%29/g' \
        -e 's/\[/%5B/g' -e 's/\]/%5D/g' -e 's/{/%7B/g' -e 's/}/%7D/g' \
        -e 's/"/%22/g' -e 's/=/%3D/g' -e 's/+/%2B/g' -e 's/\*/%2A/g' -e 's/,/%2C/g'
}

# One query per metric, each into its own file.
#
# Not one combined `or`: these series carry identical labels apart from __name__, so a set
# operation between them keeps one and drops the rest — which reported every store as having
# no budget, convincingly and wrongly. Files rather than shell variables because the JSON goes
# on to be read by python, and nesting a command substitution inside a python heredoc is a
# quoting problem waiting to be got wrong.
fetch() {
    $COMPOSE exec -T prometheus wget -qO- --timeout=15 \
        "http://localhost:9090/api/v1/query?query=$(enc "$2")" > "$WORK/$1.json" 2>/dev/null || true
}

fetch now     'dcms:store_disk_peak_bytes'
fetch budget  'dcms_store_disk_budget_bytes'
fetch growth  'dcms:store_disk_growth_bytes_per_second'
fetch horizon 'dcms:store_retention_seconds'
fetch proj    'dcms:store_disk_projected_bytes'
fetch stamp   'time() - min(dcms_store_usage_scrape_timestamp_seconds)'
# How much history the growth trend is actually computed from. A day of samples versus an hour
# of samples, self-calibrating against whatever the scrape interval happens to be: if the
# 24-hour window does not hold roughly 24 times the one-hour count, the series has not existed
# for a day and the trend is extrapolating a store's initial fill — empty to steady state — as
# though it were permanent growth. That is the difference between a projection and a scare.
fetch hist6   'count_over_time(dcms_store_disk_bytes[24h])'
fetch hist1   'count_over_time(dcms_store_disk_bytes[1h])'
fetch avail   'node_filesystem_avail_bytes{mountpoint="/"}'
fetch ratio   'dcms:host_disk_used_ratio'
fetch full    'dcms:host_disk_seconds_to_full'

python3 - "$WORK" <<'PYEOF'
import json
import os
import sys

work = sys.argv[1]


def load(name):
    """{store: value} for a per-store metric."""
    out = {}
    try:
        with open(os.path.join(work, name + ".json")) as f:
            d = json.load(f)
    except Exception:
        return out
    for r in d.get("data", {}).get("result", []):
        store = r["metric"].get("store")
        if store:
            out[store] = float(r["value"][1])
    return out


def scalar(name):
    """The single value of a one-series query, or None."""
    try:
        with open(os.path.join(work, name + ".json")) as f:
            d = json.load(f)
        return float(d["data"]["result"][0]["value"][1])
    except Exception:
        return None


def gb(n):
    return n / (1024 ** 3)


print("Telemetry retention budget — docs/adr/0008-observability.md")
print()

age = scalar("stamp")
if age is None:
    print("No measurements. Is the store-usage container running, and has Alloy picked up")
    print("the textfile directory?   docker compose ps store-usage")
    raise SystemExit(1)

note = "   ** STALE — the sidecar has stopped; these numbers are frozen. **" if age > 1800 else ""
print("Measurements are %.0f s old.%s" % (age, note))
print()

now, budget = load("now"), load("budget")
growth, horizon, proj = load("growth"), load("horizon"), load("proj")
hist6, hist1 = load("hist6"), load("hist1")

# True once every store has at least ~6h of samples behind its growth rate.
mature = bool(hist6) and all(
    hist6.get(st, 0) >= 23 * hist1.get(st, 1) for st in hist6)

if not mature:
    print("NOTE: less than a day of history. The growth trend below is still measuring each")
    print("      store's initial fill, so the projections read high. Come back later; the")
    print("      alert on the same numbers carries the same guard and will not fire yet.")
    print()

over = 0
provisional = 0
hdr = "%-12s%11s%13s%11s%12s%10s  status" % (
    "store", "peak/6h", "growth/day", "retention", "projected", "budget")
print(hdr)
print("-" * len(hdr))

for store in sorted(now):
    n = now.get(store, 0.0)
    b = budget.get(store, 0.0)
    g = growth.get(store, 0.0) * 86400
    h = horizon.get(store, 0.0) / 86400
    p = proj.get(store, n)

    if b <= 0:
        status = "no budget set"
    elif p > b:
        status = "OVER BUDGET at retention" if mature else "over budget (provisional)"
        over += 1 if mature else 0
        provisional += 0 if mature else 1
    elif p > b * 0.75:
        status = "approaching budget"
    else:
        status = "ok"

    print("%-12s%9.2fG%11.2fG%10.0fd%11.2fG%9.2fG  %s"
          % (store, gb(n), gb(g), h, gb(p), gb(b), status))

print()
print("  peak, not current size: these stores sawtooth. Alloy's write-ahead log ramps and")
print("  truncates every couple of hours; Prometheus writes blocks and compacts them. What")
print("  the disk has to hold is the top of that, and what 'growing' means is whether the")
print("  top is rising — hence a trend over a day of peaks rather than a slope through the")
print("  raw series, which would just measure whichever edge it happened to span.")
print()
print("  projected = peak plus that trend, carried to the store's own retention horizon.")
print("  That is the number that matters: a store two weeks into a thirty-day window looks")
print("  healthy right up until it is not, and once the plain over-budget alert fires there")
print("  is nothing left to do but delete data.")
print()

if over:
    print("  %d store(s) projected over budget." % over)
    print("  If Tempo is one of them, the fix is already written and commented out:")
    print("  infra/observability/alloy/config.alloy ships tail sampling set to keep 100%,")
    print("  with errors-and-slow-plus-baseline policies below it. Uncomment those before")
    print("  shortening retention — a shorter window breaks the trace-id support workflow")
    print("  for everyone, while sampling only drops the uneventful traces.")
elif provisional:
    print("  %d store(s) projected over budget, on history too short to trust — see the note" % provisional)
    print("  above. Prometheus in particular cannot actually exceed its budget: it is capped")
    print("  with --storage.tsdb.retention.size and drops the oldest blocks to stay under.")
    print("  A projection over the cap means the 30-day retention will not be reached in")
    print("  practice, not that the disk will fill.")
else:
    print("  All stores projected inside budget.")

print()
print("Host disk:")
avail, ratio, full = scalar("avail"), scalar("ratio"), scalar("full")
if avail is None:
    print("  no filesystem metrics for / — is rootfs_path set on the unix exporter?")
else:
    pct = "%.0f%% used" % (ratio * 100) if ratio is not None else "usage unknown"
    print("  %.1f GiB free, %s" % (gb(avail), pct))

if full is None:
    print("  fill-rate projection not available yet — deriv needs six hours of history")
elif 0 < full < 20 * 365 * 86400:
    print("  at the last six hours' fill rate, full in %.0f days" % (full / 86400))
else:
    # The recording rule clamps the denominator so a flat disk does not divide by zero,
    # which turns "not growing" into a number with twelve digits in it. Twenty years out
    # is not a projection, it is the clamp showing through.
    print("  not filling measurably over the last six hours")

raise SystemExit(min(over, 250))
PYEOF
