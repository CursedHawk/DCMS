# ADR 0012: Platform notifications, addressed to a role rather than to people

**Status:** accepted (2026-09-07) · **Extends** [ADR 0009](0009-in-app-notifications.md) ·
**Related:** [ADR 0011](0011-wildcard-tls-dns01.md)

## Context

The platform could not tell its own operators that anything had happened to it.

ADR 0009 built a bell for tenant admins, and it is thorough: consumers on every
`*_EVENTS` stream, recipients resolved by permission at raise time, a SignalR hub with a
Redis backplane, idempotency enforced by a unique index. All of it is scoped to a tenant.

The platform plane had nothing. That did not matter much while every failure belonged to
a tenant, and it started to matter the moment one certificate began covering every
hostname DCMS serves. The failure that prompted this is instructive: the platform
wildcard could not be issued, the edge logged the reason correctly on every sweep, and
the only way to find out was to open the Certificates page and look — or to read the
container logs. Nothing arrived. An operator learns their TLS is broken when a browser
tells them, which is late by design.

The states worth telling someone about are precisely the ones ADR 0011 made distinct:

- a certificate was issued or renewed,
- the CA **refused** — costs an attempt against the weekly ceiling, and the fix is
  usually a DNS record,
- the order was **never placed** — costs nothing, and the fix is in our own configuration,
- a certificate is inside its renewal window and has not been replaced,
- a certificate has expired, which means every hostname under it is refusing TLS.

## Decision

**A second, small notification model for facts that have no tenant, addressed to the
SuperAdmin role rather than to a list of people, and polled rather than pushed.**

Two tables in the existing `notifications` schema, written by admin-api, read by the
platform console's bell: `platform_notifications` and `platform_notification_reads`.

### Why not the tenant tables

Both tenant tables derive from `TenantEntity`. Every row carries a `TenantId`, the query
filter compares it to the ambient tenant, and idempotency comes from
`UNIQUE (TenantId, DedupeKey)`. A platform notification has no tenant, and the obvious
workaround — a sentinel `Guid.Empty` "platform tenant" — would make each of those
mechanisms mean something different depending on the row, in the one part of the system
where a scoping mistake shows one tenant another tenant's business. Two narrow tables are
cheaper than one overloaded concept.

### Why the audience is a role, not a list

ADR 0009 resolves recipients on write and gives three good reasons: the badge stays an
indexed count, a permission granted next week cannot retroactively reveal last week's
news, and a notification can never reveal something its recipient could not have opened.

Here it is not available. The audience is every holder of the **SuperAdmin global role**,
and that role lives in identity — a service admin-api does not read users from. Giving the
notification writer a reason to enumerate identity's user table so that a bell can draw a
number is a worse trade than the alternative.

So there is no fan-out. One row per fact; read state is created when an operator first
acts on it; "unread" means *this operator has no read-state row*. Consequences:

- The unread count is an anti-join rather than an indexed count. Affordable because of
  what these are: the tenant tables take a row per publish and per upload, this one takes
  a row when a certificate changes state — single figures per week.
- The endpoint's `IsSuperAdmin` check **is** the access control, where the tenant
  endpoints deliberately have none. Stated explicitly because "no permission check here"
  is correct in one file and a hole in the other.
- A "mark all read" must **insert** rows, not only update them. An `UPDATE ... WHERE
  ReadAt IS NULL` touches nothing on a first visit, when everything is unread — the badge
  would refuse to clear in exactly the case the button exists for.

### Why polled, not pushed

The tenant bell holds a SignalR connection because an admin waiting on a build wants the
toast as it lands. What arrives here is a certificate changing state: a handful of events
a week, none worth a socket. A 60-second poll costs no connection through the edge and no
group management for an audience defined by a role rather than a tenant.

### Why the worker reads a ledger instead of consuming an event

Every certificate outcome already lands in `edge.managed_certificate_attempts`. That
table is the rate-limit guard, so it cannot be skipped or forgotten, and since ADR 0011 it
distinguishes the three outcomes an operator would act on differently. An event carrying
the same facts would be a second path that can disagree with the first, and it would
require the edge to reach a schema it holds no grant on.

`CertificateNotificationWorker` re-reads a 24-hour window every two minutes and lets the
dedupe key decide what is new. A stored watermark would have to be right across restarts,
replicas and clock skew to avoid losing a notification; a unique index is right by
construction. The window is also what stops the first pass after deployment announcing
every attempt ever recorded.

### Why the text is not stored

The row carries `Kind` and a JSON bag of facts; the console renders the sentence. Prose
written at raise time is frozen against a certificate that has since been renamed and
cannot be improved without rewriting history. The tenant model reaches the same conclusion
through i18n keys; the platform console ships one language and hardcodes every other
string it shows, so the sentence lives in `render.ts` instead. The shape is the one i18n
would need, so that stays possible.

The single exception is a certificate authority's own error message, which is a
*parameter*: reproducing it verbatim is the entire value of showing it.

## Consequences

- **Neither table carries a `TenantId`**, which keeps them legitimately outside
  `RlsConfigurator.TenantTables` rather than missing from it — the same reasoning as the
  edge's certificate tables, and see [ADR 0005](0005-rls-defense-in-depth.md). If either
  ever gains a tenant column it must be registered there and in `AssertRlsCoverage` in the
  same commit.
- **Opening the bell marks nothing read.** The badge has to survive a glance, because what
  it counts is the platform's own failures. It clears on three deliberate acts: opening an
  item, dismissing it, or "mark all read".
- **Dismissing is per-operator and is not deleting.** One operator clearing their bell
  must not decide for the others that the news has been seen.
- **The kinds are duplicated across the boundary** — constants in
  `PlatformNotificationKinds`, wording in `render.ts`. A typo on either side degrades to
  showing the raw discriminator rather than failing, which is the right failure mode for a
  console that may be older than the server it is talking to, but it does mean the two
  lists have to be changed together.
- **Certificates are the only producer today.** The publisher takes a kind, a severity and
  a dedupe key and knows nothing about certificates; a second producer is a call, not a
  redesign.
- **There is no retention worker for these yet.** `NotificationRetentionWorker` sweeps the
  tenant tables only. At single figures per week this is years away from mattering, and
  wiring a sweep for it now would be maintaining a thing nothing needs.
