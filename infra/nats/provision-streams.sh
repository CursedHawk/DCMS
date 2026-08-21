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
#   * --max-age 168h would silently discard anything not drained within a week.
#   * --discard old silently trims the backlog under pressure. Audit publishers must get a
#     visible error instead, so --discard new.
#
# Note this stream is fan-out only. The system of record is the Postgres audit schema, which
# every service with database access writes transactionally; only email-worker (no database)
# and site-builder (confined to the sites schema) publish their records here.
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
    --max-age 0 \
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
