#!/bin/sh
# Idempotent JetStream provisioning. Mirrors Dcms.Shared.Contracts/Messaging/Streams.cs
# — keep both in sync. Work-queue streams (MEDIA, SITES) use workqueue retention;
# event streams use limits retention with 7-day age cap.
set -e

NATS_URL="${NATS_URL:-nats://nats:4222}"

# Wait for NATS to accept connections.
for i in $(seq 1 30); do
  if nats --server "$NATS_URL" account info >/dev/null 2>&1; then
    break
  fi
  echo "waiting for nats ($i)..."
  sleep 1
done

ensure_stream() {
  name="$1"; subjects="$2"; retention="$3"
  if nats --server "$NATS_URL" stream info "$name" >/dev/null 2>&1; then
    echo "stream $name exists"
  else
    nats --server "$NATS_URL" stream add "$name" \
      --subjects "$subjects" \
      --retention "$retention" \
      --storage file \
      --replicas 1 \
      --max-age 168h \
      --discard old \
      --max-msgs=-1 --max-bytes=-1 --max-msg-size=-1 \
      --dupe-window 2m \
      --no-allow-rollup --no-deny-delete --no-deny-purge \
      --defaults
    echo "stream $name created"
  fi
}

# AUDIT does not fit ensure_stream's defaults, in three ways that all matter:
#
#   * retention must be "limits", never "work". A work queue removes a message once it is
#     acked and allows only one consumer per subject filter — which would forbid the extra
#     sinks (webhooks, Loki, SIEM) this stream exists to feed.
#   * the default 168h max-age would silently discard anything not drained within a week.
#   * --discard old silently trims the backlog under pressure. Audit publishers must get a
#     visible error instead, so --discard new.
#
# Note this stream is fan-out only. The system of record is the Postgres audit schema, which
# every service with database access writes transactionally; only email-worker (no database)
# and site-builder (confined to the sites schema) publish their records here.
#
# ----------------------------------------------------------------------------
# AUDIT_MAX_AGE: why this is no longer 0, and what changing it costs.
#
# This stream was created with --max-age 0, meaning never expire. Combined with
# --discard new, that is a slow trap: the stream grows without bound and, when the
# disk or a byte limit is eventually reached, it starts REFUSING new audit
# publishes. The mechanism designed to make an audit write fail loudly rather than
# vanish silently turns into the thing that makes audit writes fail.
#
# 30 days is the compromise. It is long enough that a consumer being down for a
# fortnight loses nothing — which was the original objection to a 7-day cap, and a
# fair one — and short enough that the stream cannot quietly become the largest
# thing on the host. --discard new is kept exactly as it was: under pressure a
# publisher must still see an error.
#
# What this does NOT risk: losing audit history. Postgres is the system of record
# and keeps 400 days of partitions plus permanent chain anchors. This stream is
# fan-out to sinks outside the platform, and a sink that has been offline for a
# month has a bigger problem than a gap.
#
# ensure_audit_stream never reconfigures an existing stream, so on vps1 this takes
# effect only after an explicit `nats stream edit AUDIT --max-age=720h`. That is
# deliberate: it is a policy change to an audit component and should be a decision
# somebody makes, not something a deploy does to them. See the runbook.
# ----------------------------------------------------------------------------
AUDIT_MAX_AGE="${AUDIT_MAX_AGE:-720h}"

ensure_audit_stream() {
  if nats --server "$NATS_URL" stream info AUDIT >/dev/null 2>&1; then
    echo "stream AUDIT exists"
    # ensure_stream never reconfigures an existing stream. For AUDIT that is worth calling
    # out: a stream created with the wrong retention has to be edited by hand. See the runbook.
    return
  fi
  nats --server "$NATS_URL" stream add AUDIT \
    --subjects "audit.>" \
    --retention limits \
    --storage file \
    --replicas 1 \
    --max-age "$AUDIT_MAX_AGE" \
    --discard new \
    --max-msgs=-1 --max-bytes=-1 --max-msg-size=-1 \
    --dupe-window 2m \
    --no-allow-rollup --no-deny-delete --no-deny-purge \
    --defaults
  echo "stream AUDIT created"
}

ensure_stream TENANCY      "tenant.>,plugin.instance.>,membership.>" limits
ensure_stream CMS          "content.>"                               limits
ensure_stream MEDIA        "media.process.>"                         work
ensure_stream MEDIA_EVENTS "media.processed,media.failed"            limits
ensure_stream SITES        "site.publish.>"                          work
ensure_stream SITES_EVENTS "site.published,site.build.failed"        limits
ensure_stream ANALYTICS    "analytics.>"                             limits
ensure_stream CHAT         "chat.>"                                  limits
ensure_stream EMAIL        "email.>"                                 work
ensure_audit_stream

echo "JetStream provisioning complete."
nats --server "$NATS_URL" stream ls
