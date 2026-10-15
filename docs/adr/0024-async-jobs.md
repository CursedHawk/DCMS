# ADR 0024: One ledger for background work, live progress, and one operation per idempotency key

**Status:** accepted (2026-10-15) · implemented (kanban epic #303)

## Context

DCMS ran plenty of work in the background, and none of it was visible as work:

- **Drive import:** a Google Drive import downloaded up to a gigabyte inside one request per
  file. The browser looped over the files, so leaving the page lost the queue.
- **Media conversion:** it had a status column and no progress.
- **Site builds:** a build moved to `Building` without telling anyone. The deployments view sat
  on "Queued" until the build finished.
- **Dynamic Apps bulk operations:** they were synchronous and refused past 1,000 rows (500 ids).
- **Dynamic Apps flow runs:** they had the only real run model, visible only inside the plugin.
- **Plugin jobs:** `PLUGIN_JOBS` had no status at all.
- **Idempotency:** nothing at the HTTP layer. The SPA's own 401 renew-and-replay could perform
  a POST twice.

## Decision

### The ledger, not the queue

**One table, `jobs.jobs`.** It holds every tenant's background work and the platform's own.
A row is the record of one unit of work: kind, i18n title, status, progress (`Done`/`Total`,
where a null total means indeterminate), stage, error, result, attempts and lease.

**Where the work waits is the kind's business:**
- `JOBS`, one work-queue stream with a subject per lane, for the generic worker.
- `MEDIA` and `SITES` for theirs.
- `apps.flow_runs` for Dynamic Apps.

Each reports into the ledger. Moving MEDIA and SITES onto JOBS was rejected: their per-subject
lanes exist to prevent head-of-line blocking, and that reasoning (see `Streams.cs`) still holds.

**A null `TenantId` is a platform job.** ADR 0012 chose two tables over a sentinel tenant. Here
NULL needs no sentinel, because both RLS policies already treat it correctly:
- `tenant_isolation` is an equality test, which NULL never satisfies.
- `platform_scope` admits it.

The EF filter says the same explicitly: `TenantId != null && TenantId == current`. Without that,
a tenant-less context would read `TenantId == null` as "the platform's rows". The dedupe index is
`NULLS NOT DISTINCT`, filtered to rows that carry a key.

**The row is the outbox.**
- A job is committed before its run request is published, with `Nats-Msg-Id {id}:{attempt}`.
- A sweep re-sends anything still Queued after two minutes, or Running under a lapsed lease.
- A run request is only a pointer. The worker *claims* the row with one conditional UPDATE, so
  a duplicate request, a redelivery after success, or a job canceled while queued finds nothing
  to claim.

**Retries are the row's decision.** They come from its attempt count, not JetStream's
`MaxDeliver`, which cannot tell a failure from a shutdown. `JobFailedException`, and a plugin's
own validation and conflict refusals, fail a job at once.

**Workers without a grant on the schema report over the bus.** media-worker and site-builder
publish `jobs.state` (coalesced to one a second per job) and admin-api folds it in. Two rules make
arrival order matter little:
- A finished job never changes again.
- `Done` never goes backwards.

**A report is bound to its job.**
- Every ledger statement names the job's id *and* tenant, so the owner connection (which bypasses
  RLS) cannot reach another tenant's job either.
- A null tenant matches platform jobs only.
- A bus report may not touch a job that a job worker runs.

**Groups.** A job on the `group` lane is run by nobody. Its status and progress are recomputed
from its children on every child change. A Drive import is one group: one download per file,
plus each file's conversion.

**The ledger writes are not audited statement by statement.** A progress tick would otherwise be
a `data.bulk.updated` record every second. The actions behind a job are audited where they are
asked for: the import, the publish, a cancel, a retry.

### Who sees a job

**Tenants.** A member sees a job if any of these holds:
- They hold its `RequiredPermission` (the permission gating the feature it belongs to).
- It has none.
- They started it.

This is checked at read time, unlike notifications (ADR 0009): a job is live state, not a record
of news. Cancel and retry ask the same question. A Drive import requires `media:write`, because
retrying one downloads again with the importer's Drive grant.

**The console.**
- It reads every tenant's jobs and the platform's own through platform-api, behind the new
  `platform:jobs:read` and `platform:jobs:manage` permissions.
- An unexpected failure's detail (an exception message) is shown to operators and withheld from
  tenants.
- The overview reads each work queue's backlog from JetStream, including a stream nobody drains,
  which is the missing-worker case.

**Where a job came from.** Every job carries an origin, read from the request context it was
enqueued under (the same context its worker restores):
- the kind of actor,
- a person's email or name, or a client's, plugin's or service's id,
- the process that enqueued it.

A job queued by a worker on someone's behalf, such as a Drive file's conversion, names that
person. A job with no captured context reads as automatic.

Operators see all of it, plus the originating trace id. A tenant sees less, because a job is
visible to everyone holding the feature's permission, not just to those who may read the audit
log:
- an email only for one of its own members, taken from the membership directory,
- never a platform operator's email, a service client's id, or which process queued the job.

### Push

**Every ledger write publishes `jobs.changed`.** The message carries id, status and counts, and
never a title. It is sent after the write and is best-effort.

**admin-api** pushes it as `JobChanged` on the notification hub's tenant group. Like
`ResourceChanged`, it reveals nothing the recipient's next request would not; the SPA fetches a
job it does not know before showing it.

**platform-api** pushes it only to the `platform:jobs` group, made up of operators holding
`platform:jobs:read`, because a change names its tenant.

**Both consoles patch their cache in place.** A progress tick costs no request. They refetch only
for a job they have not seen, or one that just started or finished.

**Queue depth is pushed by a server-side sampler.** Nothing announces a queue's depth changing,
and the SPAs may not poll, so `PlatformSampleBroadcaster` sends `jobqueues` every 15 seconds while
a console is open.

**This also repaired the console hub, which had never worked.** Its token travels in
`?access_token=`, the only place a browser WebSocket can put it, and nothing read it from there.
`AcceptQueryStringTokenFor` reads it, for the hub's path only.

### jobs-worker: admin-api's image in a worker role

admin-api runs as `Dcms:Role`:
- **`api`:** serves requests and holds the sockets.
- **`worker`:** the jobs-worker container. It runs:
  - the tenant and platform job lanes,
  - every plugin's event handlers, jobs, scheduler and hosted services
    (`PluginHost.RunsBackgroundWork`),
  - the ledger's housekeeping.
- **`all`:** both, for development and tests.

**Why not a `Dcms.JobsWorker` project.** The plugin workers need everything admin-api composes:
- its data contexts and platform contracts,
- its identity and AI clients,
- the realm, Meta and Drive credentials in `secret/dcms/admin-api`.

A separate project would duplicate that composition, and this repository has already paid for two
lists that drifted apart.

**So the worker extends admin-api in every compose overlay and changes one variable.** It:
- reads admin-api's Vault path (`secretsOf`) under admin-api's AppRole,
- names itself in telemetry,
- splits the 35-connection pool 25 (api) / 10 (worker), keeping the documented 90-of-97 budget.

CI builds nothing new.

**email-worker stays email-only.** The plan was to rename it to platform-worker for non-tenant
jobs. Those jobs need admin-api's code and database, which email-worker deliberately lacks, so
the platform lane runs in jobs-worker. Its own lane keeps tenant backlog from standing in front
of it. This was decided with the user.

### Plugins: `dcms.jobs@2`

`IPluginTrackedJobs` has three operations:
- **`EnqueueAsync`:** a declared job as a tracked job, run by jobs-worker. The handler finds its
  run's progress, cancellation and result through `PluginJobRun.Current`.
- **`TrackAsync`:** shows work the plugin runs itself on its own queue.
- **`ReportAsync`:** moves that tracked work on. It is confined to the caller's own tracked work.

`@1` and the interval scheduler stay on `PLUGIN_JOBS`, untracked. A once-a-minute job per tenant
does not belong on a ledger people read.

### What became a job

- **Google Drive import.** A group of per-file downloads. The picker's token is sealed with Data
  Protection, and each file's conversion joins the group.
- **Media conversion.**
  - Images: per variant.
  - Video and audio: ffmpeg's clock across the renditions.
- **Site builds.** The job id is the build id. Builds are not cancelable, because site-builder
  cannot see the ledger.
- **Dynamic Apps imports, bulk updates and deletes past the request ceilings**, up to 100,000 rows
  or 20 MB of CSV. They are still all or nothing: one transaction, with the bar following the
  rows.
- **Dynamic Apps flow runs started by hand.** Scheduled and event-triggered runs are not. A
  five-minute schedule is 288 runs a day, and one import can set off tens of thousands. Scheduled
  runs were tracked at first; on dev they filled the popover within an hour.

**Left synchronous, on purpose:**

| Workload | Why it stays synchronous |
|---|---|
| API-connection refresh | One bounded fetch, and the editor wants the result. |
| Usage recalculation | Rate-limited, measured daily anyway, seconds long, and both consoles show the snapshot it returns. |
| Meta "sync now" | Not live: blocked on App Review. |
| Site deletion | The rows go in the request; storage clean-up is already best-effort afterwards. |
| Loki and Prometheus purges | Loki tracks its own delete requests; the purges are platform-api's. |
| Audit export and the client zips | They stream. |

The platform lane exists, with no handlers yet.

### One operation per `Idempotency-Key`

**`IdempotencyMiddleware` runs in admin-api, platform-api, content-api and ai-gateway.**

**First request:** claims its key in Redis and runs.

**A repeat:**
- **While the first is still running:** `409 idempotency-in-flight`.
- **After the first finished:** the stored response, marked `Idempotency-Replayed`.
- **On a different request:** `422 idempotency-key-mismatch`. "Different" means another method,
  path, query or body; an upload is compared by its length and type, never buffered.

**What is stored, and how:**
- Keys are scoped to the person: a user by id; a service by the person it propagates; anonymous
  callers by address.
- Kept bodies are encrypted with Data Protection. They are not kept at all under `no-store`.
- A 5xx, an exception, a 401, 403, 408 or 429 releases the key.
- If Redis cannot be reached, the request runs without the guard rather than every write on the
  platform failing.

**Which services require a key.**
- admin-api and platform-api refuse a person's write without one. The consoles always send one,
  minted once per call so the 401 replay is the same operation.
- Services and anonymous callers are honoured when they send a key and never refused for leaving
  it out.

Work-queue publishes carry deterministic message ids: `build:{id}`, `media:{asset}`,
`outbox:{row}`.

## Consequences

- **A new tenant table, registered everywhere such a table must be.** `jobs.jobs` is in
  `TenantTables`, the migrator, `TenantUsageMap`, the init scripts, and the test role's schema list.
- **Two streams are provisioned by the deploy.** `JOBS` and `JOBS_EVENTS`, the latter with a
  one-hour max age.
- **The kinds' text lives in the consoles.** Titles, stages and errors are i18n keys in the admin
  SPA and humanized kinds in the console, as with notifications. A key the console does not know
  falls back to the raw kind.
- **A group's progress can step back** when a child appears after others finished (a conversion
  queued after its download). It is a mean of the children it has.
- **Integration hosts run with the idempotency requirement off.** A module initializer does it,
  because their requests carry no key. The shared admin host also turns the job lanes off so its
  own claims are not raced. `IdempotencyTests` and `JobConsumerHostTests` turn each back on.
