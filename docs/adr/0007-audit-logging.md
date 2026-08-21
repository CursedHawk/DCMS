# ADR 0007: Platform-wide audit logging via a transactional outbox and an HMAC hash chain

**Status:** accepted (2026-08-21)

## Context

DCMS had no audit infrastructure. What existed instead was a scatter of ad-hoc
provenance columns — `ContentItem.CreatedBy`, `MediaAsset.CreatedBy`,
`TenantAiSettings.UpdatedBy`, `SiteBuild.GitCommitSha` — with no `UpdatedBy`
anywhere, no deletion trail, no IP or user-agent, and nothing at all for auth
events, permission denials, git operations, secret access, or the actions of
background workers.

For a multi-tenant BaaS that made several ordinary questions unanswerable: who
deleted this site, who changed this role's permissions, who read these form
submissions, was this tenant purge authorised. Tenants could not self-serve any
of it, and there was no evidence trail when a tenant disputed a change or an
account was compromised.

The requirement was that coverage be **structural rather than per-endpoint
diligence**: it has to be hard for a new endpoint, a new consumer or a new plugin
to be silently unaudited.

## Decision

Every action on tenant state produces one immutable, attributable record — who,
what, when, where, from where, with what outcome, and what changed — written by
the same transaction that commits the change.

### The write path is a transactional outbox, not a message bus

Services that own the database (`admin-api`, `identity`, `content-api`,
`ai-gateway`, and the two workers that hold a `DbContext`) insert into
`audit.audit_outbox` inside the very `SaveChangesAsync` that commits the business
change. "The change happened but the record didn't" is therefore impossible
rather than merely unlikely, which is the entire point of an audit log and which
no amount of retry logic around a publish can achieve. It is also the repo's
established idiom (`cms.content_outbox`, `identity.forgejo_sync_outbox`).

NATS carries only two things: the two services that *cannot* reach the audit
schema (`email-worker` has no database at all; `site-builder` connects as
`dcms_sitebuilder` with `USAGE` on `sites` only), and the outbound fan-out to
future sinks. Two subjects, deliberately distinct:

- `audit.submitted` — inbound, consumed by `AuditIngestConsumer` in admin-api,
  which writes into the same outbox table.
- `audit.recorded` — outbound fan-out, after the record is chained.

Routing admin-api's records — the great majority — through JetStream would have
bought nothing and cost at-least-once redelivery, out-of-order arrival, dedup
complexity, and a hard availability coupling between "can we serve a request" and
"is NATS up".

### Integrity: HMAC hash chain, one chain per tenant per month

`AuditChainWriter` drains the outbox in `Id` order under `FOR UPDATE` **without**
`SKIP LOCKED` (skipping would break chain order), and appends each record to a
chain keyed by `(TenantId, Period)` where `Period` is the first of the month.
Each monthly partition is therefore a self-contained chain, which is what makes a
retention drop cost nothing — no truncation marker, no "verified from seq N"
special case.

Canonicalisation is hand-written over the exact persisted column set, in a fixed
explicit order, with length-prefixed UTF-8 fields so `"ab"+"c"` cannot collide
with `"a"+"bc"`. `System.Text.Json` property order is not a stability contract and
a record refactor would silently invalidate history. `HashVersion` lets the
algorithm change without invalidating sealed segments.

Verification walks by `Seq`, never by `OccurredAt`. `Seq` is writer-arrival order;
a record produced earlier can be appended later after a retry, so the two orders
legitimately disagree and a verifier assuming otherwise would report tampering on
a healthy system.

### What the integrity guarantee actually is — stated plainly

**`POSTGRES_USER: dcms` is a Postgres cluster superuser** (`docker-compose.yml:39`,
`docker-compose.prod.yml:35`), and seven of eight services connect as it. A
superuser bypasses RLS, ignores `REVOKE`, and can `ALTER TABLE … DISABLE TRIGGER`.

So the append-only trigger on `audit.audit_events`, the `REVOKE UPDATE, DELETE`,
and the INSERT-only `dcms_audit_writer` role are **anti-accident controls**. They
stop a careless migration, a stray `ExecuteDelete`, and any compromised
non-superuser path. They are *not* the control against an attacker who already
holds the application's database credentials, and this ADR does not claim they
are.

The real controls are three, and none of them live in Postgres permissions:

1. **HMAC-SHA256 keyed outside the database.** The key (`Audit:ChainKey`) is held
   by the application process, never by Postgres. Full database write access does
   not let an attacker forge a valid chain. A missing key is a startup failure
   outside Development — a chain nobody can verify is worse than an obviously
   absent one. Vault (`secret/dcms/admin-api`) is the intended home; on vps1,
   where Vault is Shamir-sealed and supplementary, it is an environment variable
   from `.env`. That is a weaker custody story — the key sits on the same host as
   the database it protects — and it should move into Vault once that cluster is
   routinely unsealed. It is still outside Postgres, which is the property the
   chain actually depends on.
2. **Off-box anchors.** Each finished month is sealed into `audit.chain_anchors`
   with its own HMAC over the sequence range, row count and first/last hash. The
   anchor is also written to a `Critical` log line, which is not an error level
   here but a routing instruction: that line leaves Postgres for stdout and
   whatever collects it. An anchor that exists only in the database it attests to
   is not an off-box anchor.
3. **Omission detection.** Every process stamps each record with a
   `ServiceInstance` minted at startup and an `Interlocked`-incremented
   `ProducerSeq`. **A hash chain proves nothing about records that were never
   written** — deleting the last N entries of a chain leaves a perfectly valid
   chain, and so does a process killed between recording and flushing. A dense
   per-process counter is what turns an omission into a number, and
   `AuditGapDetector` turns that number into a `Critical` line and a metric.

**Follow-up, deliberately not folded into this work:** run the services under a
non-superuser owner role. That is a change to the Postgres provisioning and the
connection strings of all eight services, it affects migrations, and it is
independently valuable. Until it lands, control 1 is the one carrying the weight.

### Three capture layers

| Layer | Mechanism | Supplies |
|---|---|---|
| Ambient | `AuditMiddleware` | actor, tenant, IP, correlation, route, status |
| Explicit | `.WithAudit(action)` on the endpoint, `audit.Record(...)` at a call site | the semantic action and its resource |
| Automatic | `AuditOutboxInterceptor` + `AuditChangeCapture` | the redacted before→after field diff |

`.WithAudit()` opens the entry through an **endpoint filter, before the handler
runs**, so the entry rides the handler's own transaction and receives the field
diff. `WithdrawUnfulfilledDeclaration` takes it back on a ≥400 response that never
reached a save — recording an action that did not happen is worse than recording
nothing.

One action produces **one** record. `AuditChangeCapture.FindHost` folds every
entity touched in a save into the declared entry, with the rest under `related`,
rather than emitting a row per tracked entity.

### Redaction is allowlist, default-deny by entity type

An unknown entity type records its identity and its changed property *names*
only. Registered types opt in field by field, with a name heuristic
(`password|secret|token|hash|apikey|credential|private|salt|signature`) as a
second net. `TenantAiSettings.ApiKeyCiphertext`, `Invitation.TokenHash`,
`VisitorAccount.PasswordHash` and the OpenIddict tables are denied outright.
`RedactionVersion` is stored so a later policy change stays legible against old
records.

### Two schema invariants that are easy to get wrong

1. **`TenantId` is `NOT NULL`; platform scope is `Guid.Empty`, never SQL NULL.**
   The RLS policy shape (`RlsConfigurator.cs:62`) is
   `USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)`.
   `NULL = x` evaluates to NULL, which is not TRUE, so a NULL-tenant row would be
   invisible to *every* RLS-constrained reader.
2. **`AuditEventRow` does not derive from `TenantEntity`.** Deriving it would pull
   in the `HasQueryFilter(x => x.TenantId == CurrentTenantId)` convention, and the
   chain writer runs with no ambient tenant — the filter would hide every tenant's
   rows from the process whose job is to read them. The read plane scopes to a
   tenant explicitly, in the query, where a reviewer can see it.

### Retention drops partitions; it never deletes rows

`DROP TABLE` on a partition is a schema change: coarse, all-or-nothing, and
impossible to aim at one inconvenient row. A `DELETE` would fight the append-only
trigger, rewrite the table, and be the same operation an attacker would use.

**Nothing is dropped until it is sealed**, and a month that fails to seal —
because its chain does not verify — is never dropped either. That is the right way
round: the segment that most needs looking at is the one that stays.

Tenant purge deliberately excludes the `audit` schema. Adding `AuditDbContext` to
the purge sweep in `TenantAdminEndpoints.cs` would destroy the evidence of the
purge; an integration test asserts it does not happen.

## Consequences

- **Every mutating endpoint is audited, some of it coarsely.** An endpoint with no
  `.WithAudit` still produces `http.{method}.{route}` from endpoint metadata.
  Missing semantics degrade the record; they never blank it. An endpoint-
  enumeration test fails the build when a non-GET endpoint has neither audit
  metadata nor an allowlist entry.
- **The action catalog is open.** `AuditActions` is string-keyed with a
  registration API, so plugins contribute actions the way they already contribute
  `plugin:{id}:{action}` permissions. An unregistered key is still recorded —
  dropping a record because its key was unfamiliar is the one failure mode an
  audit log must never have.
- **Attribution survives the outboxes.** `ContentOutboxMessage` and
  `ForgejoSyncOutbox` carry a `ContextJson` column populated at enqueue time and
  restored per row before publish. Without it, `OutboxDispatcher`'s two-second
  poll would publish `content.published` with no actor, and "the site build
  attributes back to the human who clicked publish" would silently not work.
- **A propagated actor is an assertion, not an authentication.**
  `ActorAttribution` distinguishes `Direct` / `Propagated` / `Inferred`; the git
  webhook is always `Inferred`, never a user.
- **`IpTrusted` is usually false.** admin-api and identity call
  `UseForwardedHeaders` with all proxies trusted, so `RemoteIpAddress` is
  caller-assertable off the Caddy edge. The IP is recorded and stamped untrusted,
  with the raw `X-Forwarded-For` in `Metadata`. Pinning `KnownIPNetworks` is a
  separate fix.
- **Cross-tenant auth events are resolved at query time**, not fanned out per
  membership — a duplicated `EventId` would break dedup and leak membership drift.
  The platform-scope branch of the query uses a restricted column projection: no
  raw `Metadata`, no `ResourceLabel`, so tenant A cannot learn which other tenants
  a shared user touched.
- **`GET /audit/{id}` returns 404, not 403,** for a record belonging to another
  tenant. 403 confirms the record exists.
- **The export endpoint is itself audited**, before a byte is streamed.
- **There is no `Audit:Enabled` switch.** An environment where recording can be
  turned off is an environment where someone turns it off and forgets. The test
  suite runs with audit on, which is the point: it exercises the real write path
  rather than a parallel one only tests use.
- **The `audit` schema DDL in `infra/postgres/init/*` only runs on a fresh
  cluster.** vps1 needs the equivalent applied by hand — see `docs/runbook.md`.
- **The AUDIT JetStream stream must be `limits` retention, not `work`.** A
  work-queue stream removes messages on ack and permits one consumer per subject
  filter, which would forbid the additional sinks the fan-out exists for. It also
  needs a long or zero `--max-age` and `--discard new`, so a publisher gets a
  visible error rather than silent trimming. `provision-streams.sh` creates
  streams only when missing, so an existing stream needs `nats stream edit`.
- **Chained appends serialise on a row lock per (tenant, month).** That is
  intentional and is why a save touching more than ~50 entities collapses to one
  summary record with per-type counts instead of thousands of chained rows.
