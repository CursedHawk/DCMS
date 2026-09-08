# ADR 0013: The platform console gets a socket of its own

**Status:** accepted (2026-09-08) · **Supersedes the "polled, not pushed" decision in**
[ADR 0012](0012-platform-notifications.md) · **Related:** [ADR 0009](0009-in-app-notifications.md),
[ADR 0003](0003-tenancy-owned-by-admin-api.md)

## Context

ADR 0012 decided the platform bell would be polled, and gave a good reason:

> What arrives here is a certificate changing state: a handful of events a week, none worth a
> socket. A 60-second poll costs no connection through the edge and no group management for an
> audience defined by a role rather than a tenant.

That was true when certificates were the only producer and the bell was the only reader. Two
things have changed since.

**The console grew pages that go stale.** Tenants can be suspended and resumed from it, and
tenant creation, suspension and resumption already travel on the TENANCY stream because
site-host depends on them. The overview counts tenants. The certificates table is the thing an
operator stares at while waiting for an order to complete, and it was refetching every 30
seconds — polling four times faster than the worker that can notice an outcome, which is asking
a question that cannot have a new answer yet.

**The console got an API of its own.** Before the P3 split there was no service that was
unambiguously the console's, so a hub would have had to live in admin-api and push to a
console it did not serve. platform-api is now that service.

The cost of the polls was never the load. It was that an operations console lies quietly: an
operator suspends a tenant in one tab and the list in the other still says Active, and there is
no way to tell "not yet" from "it did not work".

## Decision

**A push-only SignalR hub in platform-api, carrying resource-change tags and nothing else, with
the polls kept as slow fallbacks.**

### One message type, and it carries no content

The hub sends exactly one thing: `ResourceChanged { tag }`, where `tag` names a class of data —
`tenants`, `certificates`, `notifications`. The console maps a tag onto the react-query keys
that are now stale and refetches them over its ordinary authorised REST calls.

This is the same shape the tenant plane uses, and the same reason: a tag per event would put the
decision of what to refetch on the producer, which does not know what any console is showing.

It also removes the question the tenant hub had to answer carefully. Because a tag carries no
data, there is one group — every open console — rather than a fan-out keyed on who may see
what. A tag discloses nothing that the recipient's next request would not already be refused,
and that request is refused on its own merits.

### Who may hold the connection

Any operator holding at least one platform-console permission. Not because a tag would tell a
tenant user anything, but because a console socket is not something an account with no business
on this console should be able to hold open. A SuperAdmin short-circuits, exactly as
`/api/platform/me` does, so a half-seeded permission table cannot lock out the person who would
repair it.

### The producer path

admin-api publishes `platform.notification.raised` when a notification row is newly written —
carrying the id, kind and severity, and no content. platform-api consumes it, plus the three
tenant lifecycle subjects that were already on the wire, with an **ordered ephemeral consumer
delivering only what is new**: every replica must see every message because each holds its own
connections, and nothing should replay a backlog on restart, since a hint about a tenant
suspended an hour ago is noise. This is the same consumer shape, on the same stream, as
site-host's `TenantStatusInvalidator`.

The publish is **after the commit, outside its transaction, and allowed to fail**. The row is
the record; this is a hint about it. A publish that could fail the write would let a broken NATS
suppress the very warnings an operator most needs.

It fires only on a *new* row. `PlatformNotificationPublisher` dedupes by key and returns false on
every pass after the first, so announcing there would push a hint every two minutes for as long
as a certificate stayed broken.

### The polls stay

Lengthened, not deleted. The bell goes from 60s to 5 minutes and the certificates table from 30s
to 2 minutes; the overview keeps its 30-second refresh because most of what it counts happens
inside tenants and is announced nowhere.

Nothing replays what was pushed while a socket was down. An operations console that goes quietly
stale during a NATS or Redis outage is the wrong failure for the one screen someone opens when
things are wrong, so the socket makes the console feel immediate and the interval is what makes
it correct.

### What is deliberately not pushed

Object-store sizes and Loki's delete queue. Those are read from systems that tell us nothing
when they change; a tag for them would be a socket carrying no news, and their existing polls
are the only mechanism available either way.

### Toasts are decided in the browser

An Error or Warning notification interrupts; Info and Success go to the bell. That policy lives
in the console rather than the server because the server would have to fan out content to
enforce it, and the console already has the list. The first page after a reload establishes a
baseline and announces nothing — opening the console must not fire a toast for every certificate
that has been failing since last week.

## Consequences

- **A third Redis channel prefix.** admin-api uses `dcms-notify`, content-api `dcms-chat`, and
  this is `dcms-console`. One Redis serves all three, and a shared prefix cross-delivers between
  hubs that know nothing about each other.
- **TENANCY gains `platform.>`.** `provision-streams.sh` converges subjects on an existing
  stream, so this applies itself on deploy. A stream of its own would be more infrastructure
  than single figures of messages a week deserve.
- **The tags are duplicated across the boundary**, like the tenant plane's and the notification
  kinds before them. A tag either side does not recognise degrades to "nothing refetches", which
  is what lets the console and its API be deployed independently — and is also what makes a typo
  invisible, so an integration test asserts every tag the console maps is one the server can
  send.
- **The bell now follows `platform:notifications:read`, not the SuperAdmin role.** A support
  operator who can see that a certificate failed is the point of having a bell. That key is new
  in P3 and joins the read-only set by derivation.
- **`ResourceChanged` never toasts**, exactly as ADR 0009 set for the tenant plane. It
  invalidates. What interrupts is a notification, and only a severe one.
- **This does not replace ADR 0012's model**, only its transport. The rows, the role-shaped
  audience, the dedupe key and the read-state-on-first-touch design are all unchanged.
