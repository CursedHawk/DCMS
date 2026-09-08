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

# Subjects are CONVERGED on an existing stream; everything else about it is left alone.
#
# The distinction matters. Adding a subject is additive and safe -- nothing that was being
# delivered stops being delivered -- and it is the change this file actually sees, because
# Streams.cs grows a subject whenever a feature does. Leaving that to a human meant the
# script "provisioned" a stream that silently rejected the new subject on every cluster
# except a freshly created one, and the symptom is an event nobody receives.
#
# Retention is the opposite: work <-> limits cannot be edited in place, changing it decides
# whether a message survives being acked, and for AUDIT it is a compliance property. So a
# mismatch is reported loudly and changed by a person, not by a deploy.
converge_subjects() {
  name="$1"; desired="$2"

  if ! command -v jq >/dev/null 2>&1; then
    echo "  (jq unavailable; cannot verify $name's subject list)" >&2
    return 0
  fi

  info=$(nats --server "$NATS_URL" stream info "$name" -j 2>/dev/null) || return 0
  current=$(echo "$info" | jq -r '.config.subjects | join(",")')

  missing=""
  for s in $(echo "$desired" | tr ',' ' '); do
    case ",$current," in
      *",$s,"*) ;;
      *) missing="${missing:+$missing,}$s" ;;
    esac
  done

  if [ -n "$missing" ]; then
    echo "  adding subject(s) to $name: $missing"
    nats --server "$NATS_URL" stream edit "$name" --subjects "$current,$missing" -f
  fi
}

# Reports, never fixes. See converge_subjects for why this one is a person's decision.
check_retention() {
  name="$1"; desired="$2"

  command -v jq >/dev/null 2>&1 || return 0
  info=$(nats --server "$NATS_URL" stream info "$name" -j 2>/dev/null) || return 0
  actual=$(echo "$info" | jq -r '.config.retention')

  case "$desired" in
    work) want="workqueue" ;;
    *)    want="$desired" ;;
  esac

  if [ "$actual" != "$want" ]; then
    echo "  WARNING: $name has '$actual' retention, expected '$want'." >&2
    echo "  Retention cannot be edited in place; the stream has to be recreated," >&2
    echo "  which discards its backlog. Not something a deploy should decide." >&2
  fi
}

ensure_stream() {
  name="$1"; subjects="$2"; retention="$3"
  if nats --server "$NATS_URL" stream info "$name" >/dev/null 2>&1; then
    echo "stream $name exists"
    converge_subjects "$name" "$subjects"
    check_retention "$name" "$retention"
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
    converge_subjects AUDIT "audit.>"
    # Subjects converge; retention, max-age and discard policy do not. For AUDIT that is the
    # whole point -- they are compliance properties, and a stream created with the wrong ones
    # has to be edited by hand. check_retention reports it. See the runbook.
    check_retention AUDIT limits
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

# `edge.>` carries the platform's own certificate control messages. converge_subjects adds it to
# an existing TENANCY stream, so this is a normal deploy rather than a stream recreate.
ensure_stream TENANCY      "tenant.>,plugin.instance.>,membership.>,edge.>,platform.>" limits
ensure_stream CMS          "content.>"                               limits
ensure_stream MEDIA        "media.process.>"                         work
ensure_stream MEDIA_EVENTS "media.processed,media.failed"            limits
# The wildcard is load-bearing. Publishes are routed by render mode --
# site.publish.requested.staticfiles / .staticprerender / .reactapp -- so that site-builder
# can drain them on separate consumers and a 30-second React build never sits in front of a
# 340-millisecond bundle extract. `site.publish.>` already covered every one of those, which
# is why the split needed no stream change; keep it a wildcard.
ensure_stream SITES        "site.publish.>"                          work
ensure_stream SITES_EVENTS "site.published,site.build.failed"        limits
ensure_stream ANALYTICS    "analytics.>"                             limits
ensure_stream CHAT         "chat.>"                                  limits
ensure_stream EMAIL        "email.>"                                 work
# Inbound notification requests from services that cannot write the notifications
# schema. limits retention, not work: a work queue removes a message on ack and permits
# only one consumer per subject filter, which would make admin-api's ingest the only
# thing that could ever read this.
ensure_stream NOTIFY       "notify.>"                                limits
ensure_audit_stream

# ---------------------------------------------------------------------------
# Retired durables
#
# A durable consumer outlives the code that bound it, and an orphaned one holds the
# stream's ack floor down forever -- so the stream keeps every message behind it and
# grows without bound, while every outward sign says the deploy went fine. Clearing
# one used to be a runbook chore performed once, by whoever read the runbook.
#
# Add a line here when a durable's binder is deleted or converted to an ephemeral
# consumer. It is safe while the service runs: nothing binds these any more.
# ---------------------------------------------------------------------------
retire_consumer() {
  stream="$1"; consumer="$2"
  if nats --server "$NATS_URL" consumer info "$stream" "$consumer" >/dev/null 2>&1; then
    nats --server "$NATS_URL" consumer rm "$stream" "$consumer" -f
    echo "retired consumer $stream/$consumer"
  fi
}

# site-host moved to an ephemeral ordered consumer so every replica sees every
# site.published, rather than one replica seeing each.
retire_consumer SITES site-host-cache

# The pre-split publish subject. `site-builder` still binds site.publish.requested so that a
# message written by an admin-api from before the queue split is still built during the
# rolling deploy that introduces it -- a work-queue stream never delivers or removes a message
# no filter matches, so without it that build would sit in Queued until the reaper failed it.
#
# UNCOMMENT ONE RELEASE AFTER THE SPLIT SHIPPED (2026-09-03), and delete the Legacy() lane in
# src/Services/Dcms.SiteBuilder/SiteBuildLane.cs in the same change. That lane logs a warning
# for every message it drains, so the logs answer "is anything still publishing there" before
# you do.
#
# retire_consumer SITES site-builder

echo "JetStream provisioning complete."
nats --server "$NATS_URL" stream ls
