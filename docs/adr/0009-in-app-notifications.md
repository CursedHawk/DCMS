# ADR 0009: In-app notifications via admin-api, fanned out on write

**Status:** accepted (2026-09-02)

## Context

DCMS could not tell a tenant admin that anything had happened. Every asynchronous
outcome the platform produced was invisible in the UI:

- `site-builder` published `site.published` / `site.build.failed`, and the only
  consumer was site-host's cache invalidator. Nothing told the SPA a build had
  finished; the deployments view polled and the admin refreshed.
- `MEDIA_EVENTS` (`media.processed` / `media.failed`) was provisioned, published
  to, and read by **nothing at all**. A failed transcode left an asset that, as
  `MediaConsumerBase` puts it, "the tenant will notice and ask about".
- Invitation acceptance published `membership.changed`, which carries only
  `(tenant, user)` and also fires for role edits — it cannot say who accepted
  what. Invitation **expiry** had no event and no sweeper: a passive `ExpiresAt`
  column evaluated at read time, so a lapsed invitation was silent forever.
- `ChatFanoutConsumer` had been written for precisely this, with a doc comment
  saying "the notification transport is a documented extension", and only logged.

## Decision

### The hub lives in admin-api, at `/api/hub/notifications`

content-api already hosts a SignalR hub with a Redis backplane, and reusing it
would have cost no new infrastructure. It was still wrong: notifications are
permission-scoped admin-plane data, and [ADR 0003](0003-tenancy-owned-by-admin-api.md)
deliberately keeps tenancy, permissions and permission *evaluation* in admin-api.
Putting the hub in content-api would have given the delivery plane a reason to
read the tenancy schema and resolve admin permissions — the exact coupling ADR
0003 exists to avoid.

The hub is **push-only**. Marking read and dismissing go over REST, where they are
ordinary authorised requests with the tenant and audit middleware around them. Hub
methods doing the same work would be a second, differently-guarded write path onto
the same rows.

**Mounted under `/api`, not `/hub`.** `/hub/*` on the admin host already routes to
content-api, and adding a sibling route would have meant editing the Caddyfile —
which is bind-mounted and, per `docs/runbook.md`, needs a container **restart**
rather than a reload, because a plain reload reads a stale inode. Restarting the
production edge to ship a bell is a bad trade. `/api/hub/notifications` rides the
existing `/api/* -> admin-api` route, so the edge configuration is untouched. The
costs are two one-line changes: `ws: true` on the vite dev proxy, and `/api/hub`
added to the rate-limiter exemption that already lists `/health` and `/hub`.

The backplane channel prefix is `dcms-notify`. It **must** differ from
content-api's `dcms-chat`: both services share one Redis, and a shared prefix
would cross-deliver between the two hubs.

### Consumers are shared durables — the opposite of `SiteCacheInvalidator`

`SiteCacheInvalidator` uses an ephemeral *ordered* consumer so every replica sees
every message, because it mutates per-replica in-memory state. Notifications
invert that: the work is a database write, so exactly one replica must do it, and
the Redis backplane is what carries the push to browsers connected elsewhere. A
per-replica consumer here would insert N copies of every notification.

Consumers read only the `*_EVENTS` streams. `SITES` and `MEDIA` are work-queue
retention — a message is removed on ack and one consumer per subject filter is
permitted — so a consumer added there would steal jobs from site-builder and
media-worker.

### Recipients are resolved on write, not filtered on read

One `notifications.notifications` row per occurrence, one
`notifications.recipients` row per addressee, resolved at raise time from the
members whose roles grant the permission that already gates the underlying
feature. A notification can therefore never reveal something its recipient could
not have opened anyway.

The alternative — one row carrying a `RequiredPermission`, filtered per request —
was rejected on two grounds. The badge would become a permission-filtered
anti-join on every poll instead of an indexed count; and a permission granted next
week would retroactively reveal last week's notifications, which is the wrong
semantic for a record of what happened.

Consequently the read endpoints carry **no permission check**. The audience was
decided when the notification was raised, and every query filters on the caller's
own `UserId` instead. `GET /notifications/{id}` returns 404, not 403, for someone
else's — 403 confirms it exists.

### Idempotency is a unique index, not consumer bookkeeping

`UNIQUE (TenantId, DedupeKey)`. Consumers insert optimistically and treat Postgres
`23505` as "already handled", then ack. JetStream is at-least-once and a
redelivery must not notify twice; a check-then-insert would still race between
replicas, and this cannot.

**The key names the fact, never the event id.** This was originally the event id,
which reads as obviously right and is wrong: an event id identifies a *publish*,
and a producer announcing the same fact twice mints a new one each time. The first
site published after these consumers shipped produced three identical rows,
because site-builder's ack deadline was 30 seconds against a three-minute build —
JetStream redelivered the job mid-flight, the build ran three times, and each run
published `site.published` with a fresh `Guid.NewGuid()`. Three keys, three
notifications, and the unique index never saw a collision.

The producer bug is fixed (`AckHeartbeat` renews the lease while the work runs),
but the durable guarantee is the key: `site.published:{buildId}`,
`media.processed:{assetId}`, `content.published:{itemId}:{occurredAt}`. The test
is whether a recipient would call two arrivals the same piece of news. A source
scan (`NotificationDedupeKeyTests`) fails the build if a consumer goes back to the
event id.

The other subtlety is invitation expiry, where the key is
`{invitationId}:{expiresAt.UtcTicks}`. Resending an invitation rolls `ExpiresAt`
and clears `ExpiredNotifiedAt`, so the same invitation can legitimately lapse more
than once; including the deadline lets the second lapse notify while still
collapsing a redelivery of the first.

### Two write paths, mirroring audit

admin-api owns the schema and raises in-process. Services that cannot reach it
publish `notify.raise` on the new `NOTIFY` stream, which
`NotificationIngestConsumer` drains — the same shape as `audit.submitted`, and for
the same reason: one writer for the tables, reached either in-process or over the
bus. Today the only such publisher is content-api, for form submissions.

There is deliberately **no** outbound `notification.raised` fan-out. Delivery to
browsers is the hub's job, and a second copy on the bus would be another thing to
keep consistent with the table that is already the system of record.

### Text is stored as i18n keys, never as prose

`TitleKey`, `BodyKey` and a `ParamsJson` object. The SPA ships English and Czech;
a rendered English sentence written into the table at raise time would be
untranslatable forever after, and re-rendering it later is impossible once the
originating entity has been renamed or deleted.

### Toasts are severity-gated and self-suppressed

`Warning` and `Error` always toast. `Info` and `Success` toast only when the
actor is not the viewer — the notification carries `ActorUserId`, so publishing
ten pages does not toast ten times at the person who clicked publish. The bell
entry appears either way; the rule decides only whether it also interrupts.

## Consequences

- **Four of the event sources needed no producer changes at all.** `site.published`,
  `media.processed`, `content.published` and the tenancy events already existed;
  only form submissions (content-api), Meta reauth and chat needed a new call.
- **`MEDIA_EVENTS` finally has a consumer.** It had been published into a void.
- **Invitation expiry needed a new column and a new worker.** `ExpiredNotifiedAt`
  on `tenancy.invitations`, swept by `InvitationExpiryWorker` with
  `FOR UPDATE SKIP LOCKED`, is the only source here with no event behind it.
  Resend clears the stamp, or a resent invitation could never be reported again.
- **The read/dismiss endpoints are audit-exempt and suppress bulk capture.** They
  use `ExecuteUpdateAsync`, which leaves no before-image, so the interceptor would
  otherwise record a bare `data.bulk.updated` every time an admin opened the bell.
  Read state is per-user UI state on a row the caller owns; the action a
  notification describes was already audited where it happened.
- **The retention worker records one audit entry per pass**, under an advisory
  lock so only one replica sweeps — the same trade `AnalyticsRetentionWorker`
  makes, for the same reason.
- **A new schema costs nothing at deploy time.** `infra/postgres/init/*` runs only
  on an empty data directory, but `postgres-bootstrap` re-applies those scripts
  against the running cluster before every deploy, so adding `notifications` to
  `00-schemas.sql` and `01-rls.sql` is the entire change.
- **Platform-scope notifications are deliberately out of scope.** SuperAdmins hold
  no membership rows and no `tenant_role_permissions` rows, so recipient
  resolution would have to reach into the `identity` schema. Operator alerting
  already exists over email via `AlertEndpoints`.
- **`NOTIFY` is provisioned by the deploy, like every other stream.**
  `provision-streams.sh` creates it, and converges the subject list of a stream
  that already exists. Retention is left alone deliberately — it cannot be edited
  in place, and recreating a stream to change it discards the backlog.
