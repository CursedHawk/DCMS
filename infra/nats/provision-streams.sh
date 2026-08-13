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

ensure_stream TENANCY      "tenant.>,plugin.instance.>,membership.>" limits
ensure_stream CMS          "content.>"                               limits
ensure_stream MEDIA        "media.process.>"                         work
ensure_stream MEDIA_EVENTS "media.processed,media.failed"            limits
ensure_stream SITES        "site.publish.>"                          work
ensure_stream SITES_EVENTS "site.published,site.build.failed"        limits
ensure_stream ANALYTICS    "analytics.>"                             limits
ensure_stream CHAT         "chat.>"                                  limits
ensure_stream EMAIL        "email.>"                                 work

echo "JetStream provisioning complete."
nats --server "$NATS_URL" stream ls
