# ADR 0015: The database enforces tenant isolation, not just the ORM

**Status:** accepted (2026-09-22) · **Supersedes the deferral in [ADR 0005](0005-rls-defense-in-depth.md)**

## Context

ADR 0005 shipped Row-Level Security as a backstop and then deliberately declined to
make it load-bearing: the services connect to Postgres as `dcms`, the table owner, and
a table owner bypasses RLS. The policies are real, they are correct, and the isolation
suite proves them — against `dcms_rls`, a role nothing in production uses. For the
running services the policies are decoration.

So tenant isolation at runtime is exactly one thing: the EF Core global query filter
`TenantId == tenantContext.TenantId ?? Guid.Empty`, applied by hand on every tenant
entity in 16 DbContexts. The security audit rated that **acceptable but fragile** and
recorded it as ARCH-01. It is fragile in a specific way worth stating precisely, because
it decides what this ADR has to be:

- The filter is opt-in per entity. An entity mapped without `HasQueryFilter` is not
  protected, and nothing fails — no test, no build, no startup check. The table works.
- `IgnoreQueryFilters()` is called **140 times**. Every one of them was swept in the
  audit's verification pass and every one is currently safe; one of them (the IDE
  site-preview proxy, SEC-14) was not, and that is what a missing re-scope looks like:
  an ordinary-looking endpoint serving another tenant's site.
- A DbContext registered without `ITenantContext` — or with the `NullTenantContext`
  several of them carry for worker paths — has no filter at all.

Each of those is one missing line away from silent cross-tenant reads. There is nothing
underneath to catch it. `RlsConfigurator.AssertCoverage` protects the RLS *list*; it has
no opinion about query filters, and cannot have one.

The counter-argument in ADR 0005 was that making RLS load-bearing is "a large,
regression-prone change for a backstop." That was right in phase 12 and it is still the
main fact about this work. What changed is the value on the other side of the scale:
the platform now holds every tenant's content, media, analytics, visitor accounts, chat
transcripts, AI transcripts (which contain lifted tool results from tenant data), Meta
OAuth tokens and audit log, and it serves site editors who can run code.

## Decision

Tenant isolation becomes a property of the database. Concretely:

1. **The services stop connecting as the table owner.** A new role `dcms_app` —
   `LOGIN NOSUPERUSER NOBYPASSRLS`, DML on the business schemas, no DDL — becomes the
   runtime connection for every service that reads tenant data. A non-owner is subject
   to RLS without `FORCE ROW LEVEL SECURITY`, so `FORCE` is not needed and is not used;
   the owner keeps bypassing, which is what lets migrations and `RlsConfigurator` work
   at all.

2. **The `app.tenant_id` GUC is set per unit of work**, from `ITenantContext`, by
   interceptor rather than by hand. The policies already key on it (ADR 0005 shaped them
   this way on purpose), so no policy changes.

3. **Deliberate cross-tenant access becomes explicit and auditable.** A second
   permissive policy, `platform_scope`, admits any row when the GUC `app.scope` is
   `platform`. A scoped helper turns it on for the duration of one operation and off
   again, in the shape `AuditScope.SuppressBulkCapture()` already established. Every
   legitimate cross-tenant path — the workers, the outbox dispatchers, SuperAdmin
   listings, `/me/tenants`, invitation accept, the anonymous Meta OAuth callback that
   *establishes* the tenant — wraps itself in it.

This turns the failure mode inside out. Today a forgotten filter is a cross-tenant read
that looks like a working feature. Afterwards it is an empty result or a failed write:
loud, local, and wrong in the direction that does not leak.

### What this does *not* do

- It does not replace the query filters. They stay, and stay primary: they are what
  makes the common path correct and efficient (a predicate in the query beats a policy
  the planner has to apply to a wider scan). RLS becomes what it was always described
  as — the thing that catches the one that got away.
- It does not touch `identity`, `dataprotection` or `edge`, whose tables carry no
  tenant column.
- It does not change `dcms_sitebuilder`, which holds `BYPASSRLS` deliberately and
  documented (it operates cross-tenant with no ambient tenant), or `dcms_platform` and
  `dcms_edge`, which never read a tenant table.

## The constraint that shapes the rollout

**A GUC on a pooled connection outlives the request that set it.** `set_config(..., false)`
is session-scoped: leave it set and the next request served by that physical connection
inherits the previous tenant's id. That is a cross-tenant read introduced *by the
control meant to prevent one*, which is the worst available outcome and the reason this
cannot be done in one careless commit.

Two consequences, both binding:

- The GUC is set with `SET LOCAL` semantics inside the ambient transaction, or reset on
  connection return. Never a bare session `set_config` on a pooled connection.
- The same rule applies to `app.scope`. A platform scope that leaks is a tenant filter
  that silently does nothing.

There is a second, subtler ordering problem. Finbuckle resolves the tenant using
`TenancyDbContext` — the same scoped context the filters later use — so on that one path
the tenant is not known when the connection opens. Tenant resolution therefore runs
under `platform_scope`, explicitly, rather than being special-cased inside the
interceptor.

## Rollout

A push to `master` is the deploy, so this is staged. Each phase is independently
shippable, reversible, and leaves the platform working.

1. **The enforcement target, inert.** ✅ *Landed.* `dcms_app` exists with its grants
   (`infra/postgres/init/06-app-role.sh`, re-applied on every deploy by
   `postgres-bootstrap`); `RlsConfigurator` adds the `platform_scope` policy beside
   `tenant_isolation` and grants `dcms_app` DML as it goes; `AssertAppliedAsync` checks
   both policies. Nothing connects as `dcms_app` yet, so nothing changes behaviourally —
   but the target is real and the isolation tests now prove all three states against the
   role the services will actually use: no GUC sees nothing, a tenant GUC sees one
   tenant, `app.scope=platform` sees everything.

2. **The GUC, behind a flag, with the services still on the owner connection.** ✅
   *Landed.* `TenantGucInterceptor` sets both GUCs on every connection open and again
   before any command whose desired values changed since — open alone is not enough,
   because Finbuckle resolves the tenant through the context it later filters and a
   platform scope can be entered while a transaction holds the connection. It is
   registered by `AddDcmsRlsEnforcement` in the five services that read tenant tables,
   only when `Rls:Enforce` is on, and attached to every business context through the
   existing `UseDcmsAuditInterceptors` hook. `PlatformScope` is a static `AsyncLocal`
   helper, inert without the interceptor, so phase 3 can adopt it ahead of phase 4.

   Two things surfaced while building it. `ContentListQueries` and `TagQueries` opened the
   raw connection themselves, which skips EF's interceptors — under enforcement every
   content list and tag cloud would have come back empty; they now open through EF. And
   the leak protection turned out to depend on Npgsql resetting session state when a
   connection returns to its pool: the interceptor writes both values before any EF
   command, but a raw connection drawn from the same pool would otherwise inherit the last
   request's. Session `set_config` was kept over `SET LOCAL`, which would drop the values
   at a commit on a connection EF holds open while the bookkeeping believed them set. So
   the reset is now tested against a pool of one (same backend pid, both GUCs empty —
   mutation-checked with the reset turned off), and a guard refuses `No Reset On Close`
   and `Multiplexing` in any deployed connection string.

3. **The `IgnoreQueryFilters()` sweep.** All 140, decided one at a time into two piles:
   re-scoped by an explicit `TenantId` (needs nothing) or genuinely cross-tenant (wraps
   in `PlatformScope`). A test asserts every call site is one or the other, so a 141st
   has to declare which it is. This is the bulk of the work and the phase most likely to
   surface a real bug.

4. **One service at a time onto `dcms_app`**, starting with the one whose blast radius
   is smallest and whose paths are most uniform, and soaking between each. A service that
   misbehaves is moved back by changing one connection string.

5. **Remove the owner connection from the service environment** once every service is
   across, leaving `dcms` to the migration jobs alone.

The soak between phases is not ceremony. ADR 0014 staged the same way and the soak is
what caught the edge refusing every WebSocket handshake — a failure no test in the
repository would have produced.

## Consequences

- A forgotten query filter stops being a breach and becomes an empty result. So does a
  DbContext registered without `ITenantContext`.
- **Empty results become a failure mode.** A path that loses its tenant returns nothing
  instead of leaking; that is the trade being made, and it will show up as "the page is
  blank" long before anyone suspects RLS. The interceptor logs the tenant it set, at
  debug, for exactly this.
- There is a cost per unit of work: one `SET LOCAL` round trip, and policies the planner
  applies to every tenant-table query. Measured on `loadtest/` before phase 4 widens.
- `IgnoreQueryFilters()` stops being sufficient on its own. After phase 3 it means
  "this query is not filtered by EF", not "this query sees everything" — which is a
  distinction the codebase has to keep straight, and the sweep test is what keeps it.
- The audit's ARCH-01 closes with phase 5, not before. Until then this ADR is the plan
  and the phases are the status.

## Alternatives considered

- **`FORCE ROW LEVEL SECURITY` on the owner.** Same enforcement, but it also constrains
  the migration jobs and `RlsConfigurator` itself, which then need their own escape. A
  non-owner runtime role gets the enforcement without touching the DDL path, and matches
  the four least-privilege roles the platform already runs (`dcms_sitebuilder`,
  `dcms_platform`, `dcms_edge`, `dcms_grafana`).
- **A second connection string for cross-tenant work** rather than a `platform_scope`
  policy. Two DbContext registrations per context, doubled pools, and the choice of which
  to inject made at registration time rather than at the call site that knows. The GUC
  keeps the decision where the knowledge is.
- **Leaving it as ADR 0005 decided.** The audit's own severity for ARCH-01 is
  INFORMATIONAL, and the query filters are applied consistently today. But "applied
  consistently today" is a statement about a snapshot, maintained by hand, across 16
  contexts and 140 opt-outs — and it is the only thing between one tenant and another.
