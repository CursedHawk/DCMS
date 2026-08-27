#!/bin/sh
# Publishes each telemetry store's on-disk size as a Prometheus metric.
#
# WHY THIS EXISTS. The retention budget is stated in bytes on disk — Prometheus 8 GB, Loki
# 8 GB, Tempo 4 GB — and until now nothing measured that. The dashboards had ingest rates
# (bytes/sec into Loki, spans/sec into Tempo) and Prometheus's self-reported TSDB size, and
# from those you can guess at growth but you cannot answer "are we inside the budget", which
# is the only question the budget exists to answer. `du` on the host answered it, once, by
# hand — and a number a human has to remember to go and look at is not a control.
#
# WHY A LOOP IN A CONTAINER rather than a host cron. A cron entry lives outside the repo,
# outside compose, and outside anyone's mental model of the deployment; it survives exactly
# until the next person rebuilds the host. This runs as an ordinary compose service with the
# store volumes mounted read-only, deploys with everything else, and is visible in
# `docker ps` like every other moving part.
#
# WHY TEXTFILE rather than an HTTP endpoint. Alloy already runs the node exporter, and the
# node exporter already has a textfile collector for exactly this shape of problem: a number
# that some other process is better placed to compute. Writing a file needs no port, no
# server, and no health check.
set -u

TEXTFILE_DIR="${TEXTFILE_DIR:-/textfile}"
INTERVAL="${INTERVAL:-300}"
STORES_DIR="${STORES_DIR:-/stores}"

# Budgets in bytes, from docs/adr/0008-observability.md. Published as their own series so a
# panel or an alert can compute the ratio without hardcoding a number that then drifts out of
# step with the retention settings it is supposed to describe.
budget_for() {
    case "$1" in
        prometheus) echo 8589934592 ;;   # 8 GiB
        loki)       echo 8589934592 ;;   # 8 GiB
        tempo)      echo 4294967296 ;;   # 4 GiB
        grafana)    echo 536870912  ;;   # 512 MiB — SQLite plus rendered images
        alloy)      echo 268435456  ;;   # 256 MiB — WAL and position files
        *)          echo 0 ;;
    esac
}

write_once() {
    tmp="$TEXTFILE_DIR/.dcms_store_disk.prom.$$"

    {
        echo '# HELP dcms_store_disk_bytes Bytes on disk in a telemetry store data directory.'
        echo '# TYPE dcms_store_disk_bytes gauge'
        for path in "$STORES_DIR"/*; do
            [ -d "$path" ] || continue
            store=$(basename "$path")
            # -s for the total, and swallow the per-file permission noise: these mounts are
            # read-only and owned by each image's uid, so some subdirectories are not
            # readable. The total is still right to within those directories' own size, and
            # a metric is not worth failing over.
            bytes=$(du -sb "$path" 2>/dev/null | cut -f1)
            [ -n "$bytes" ] || continue
            echo "dcms_store_disk_bytes{store=\"$store\"} $bytes"
        done

        echo '# HELP dcms_store_disk_budget_bytes Retention budget for a telemetry store, from ADR 0008.'
        echo '# TYPE dcms_store_disk_budget_bytes gauge'
        for path in "$STORES_DIR"/*; do
            [ -d "$path" ] || continue
            store=$(basename "$path")
            b=$(budget_for "$store")
            [ "$b" -gt 0 ] && echo "dcms_store_disk_budget_bytes{store=\"$store\"} $b"
        done

        echo '# HELP dcms_store_usage_scrape_timestamp_seconds When this file was last written.'
        echo '# TYPE dcms_store_usage_scrape_timestamp_seconds gauge'
        echo "dcms_store_usage_scrape_timestamp_seconds $(date +%s)"
    } > "$tmp"

    # Rename rather than write in place: the node exporter may read the file at any moment,
    # and a half-written one is a parse error that drops every series in it.
    mv "$tmp" "$TEXTFILE_DIR/dcms_store_disk.prom"
}

while true; do
    write_once
    sleep "$INTERVAL"
done
