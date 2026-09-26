#!/usr/bin/env bash
# A staircase: one scenario at rising VU counts, one run.sh bundle per step, stopping at the
# knee -- the first step whose error rate or p95 breaks the given limits. Finds where a
# surface stops scaling, which a single plateau at a fixed VU count cannot.
#
#   ./loadtest/stress.sh --env vps1 --scenario delivery --steps "10 25 50 100 200" --duration 2m
#   ./loadtest/stress.sh --env vps1 --scenario media --steps "4 8 16" -- --via vps1
#
# Options after `--` go to every run.sh call unchanged. Each step prints one line --
#   vus  req/s  failed%  p95  <every tagged or custom trend's p95>  generator-load
# -- and appends it to loadtest/runs/stress-<scenario>-<env>.tsv. The generator's own load
# average is on the line because a staircase run from one box eventually measures that box:
# once it sits near its core count, the next step's numbers are about the generator.
#
# Exempt the generator's address first (seed/exempt.mjs), or every step measures the edge's
# per-IP limiter at ~20 req/s.

set -uo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ENVNAME=local; SCENARIO=""; STEPS=""; DURATION=2m; MAX_FAIL=5; MAX_P95=5000
while [ $# -gt 0 ]; do
    case "$1" in
        --env) ENVNAME="$2"; shift 2 ;;
        --scenario) SCENARIO="$2"; shift 2 ;;
        --steps) STEPS="$2"; shift 2 ;;
        --duration) DURATION="$2"; shift 2 ;;
        --max-fail) MAX_FAIL="$2"; shift 2 ;;     # percent
        --max-p95) MAX_P95="$2"; shift 2 ;;       # ms, overall http_req_duration
        --) shift; break ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
[ -n "$SCENARIO" ] && [ -n "$STEPS" ] || { sed -n '2,16p' "$0"; exit 2; }
TSV="$REPO/loadtest/runs/stress-$SCENARIO-$ENVNAME.tsv"
mkdir -p "$REPO/loadtest/runs"

for vus in $STEPS; do
    log="$(mktemp)"
    "$REPO/loadtest/run.sh" --env "$ENVNAME" --scenario "$SCENARIO" --vus "$vus" --duration "$DURATION" "$@" 2>&1 | tee "$log" >/dev/null
    dir="$(sed -n 's/^   output  //p' "$log" | head -1)"; rm -f "$log"
    load="$(cut -d' ' -f1 /proc/loadavg)"
    line="$(node -e '
      const [dir, vus, load, maxFail, maxP95] = process.argv.slice(1);
      let m; try { m = require(dir + "/k6-summary.json").metrics; } catch { console.log(`${vus}\tNO SUMMARY\t${dir}`); process.exit(3); }
      const v = (k) => m[k]?.values ?? m[k] ?? {};
      const fail = 100 * (v("http_req_failed").rate ?? v("http_req_failed").value ?? 0);
      const p95 = v("http_req_duration")["p(95)"] ?? 0;
      const extra = Object.keys(m).filter((k) => k !== "http_req_duration" && !k.includes("expected_response")
          && v(k)["p(95)"] !== undefined && (k.startsWith("http_req_duration{") || !k.startsWith("http_req_")) && !k.startsWith("iteration"))
        .map((k) => `${k.replace("http_req_duration", "")}=${Math.round(v(k)["p(95)"])}`).join(" ");
      console.log([vus, (v("http_reqs").rate ?? 0).toFixed(1), fail.toFixed(2) + "%", Math.round(p95) + "ms", extra, "gen-load=" + load].join("\t"));
      process.exit(fail > Number(maxFail) || p95 > Number(maxP95) ? 1 : 0);
    ' "$dir" "$vus" "$load" "$MAX_FAIL" "$MAX_P95")"
    knee=$?
    echo "$line" | tee -a "$TSV"
    [ "$knee" -eq 0 ] || { echo "== knee at $vus VUs ($SCENARIO): stopping the staircase"; break; }
done
