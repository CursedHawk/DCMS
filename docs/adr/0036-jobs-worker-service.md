# ADR 0036: jobs-worker is its own service; admin-api serves admin requests only

**Status:** accepted (2026-11-18) · kanban epic #650 · amends ADR 0024 §"jobs-worker"

## Context

ADR 0024 made jobs-worker admin-api's image in a worker role (`Dcms:Role`). Only part of the
background work was split by that role: the job lanes, the ledger housekeeping and the plugins'
workers. **23 of admin-api's own hosted services had no role check** and ran in both containers,
kept from doing the work twice only by their locks:
- the CMS outbox and scheduled publish;
- the audit chain writer, ingest, projector and maintenance;
- the nine notification consumers and the invitation expiry;
- the membership cache invalidation;
- the retention sweeps (analytics, notifications, AI conversations);
- the API-connection refresh, the certificate notifications and tenant usage.

On 2026-11-18 the audit maintenance ran **admin-api** out of memory while sealing October's
887 MB of audit rows, and console requests caught in that moment answered 500.

**Asked for (user, 2026-11-18):**
- jobs-worker becomes its own container and image;
- admin-api serves only admin requests;
- every worker process is visible as a background job: tenant-bound in the tenant's jobs,
  platform-bound in the platform console;
- every service stays stateless, so scaling is running more containers of it (production
  Kubernetes is planned);
- later services may split the work further (a plugin-worker, a platform-worker).

## Decision

1. **Three projects, one composition.**
   - `src/Shared/Dcms.Shared.AdminPlane` holds what both services need: the domain services,
     the hubs' types, the job contracts, and `AddDcmsAdminPlane(serviceName,
     runsBackgroundWork)`. That one composition method covers data, messaging, the SignalR
     backplane, plugins and service clients, so the two services cannot drift. This answers
     ADR 0024's reason for not having a separate project.
   - `src/Services/Dcms.AdminApi` holds endpoints, middleware, hubs, the socket push
     (`JobLiveUpdates`) and the migrator. It registers no background work and passes
     `runsBackgroundWork: false`.
   - `src/Services/Dcms.JobsWorker` holds every hosted service and job handler, in modules:
     `AddJobLanes`, `AddEventConsumers`, `AddOutboxes`, `AddSweeps` and `AddPluginWork`. It
     maps only `/` and the health checks.
   - **Future workers.** A plugin-worker or platform-worker is a service that calls a subset of
     these modules. No switches exist until that service does.

2. **Types keep their `Dcms.AdminApi.*` namespaces.**
   - The hubs must keep theirs: the Redis backplane names its channels after the hub type, so a
     rename splits admin-api from jobs-worker during a rolling deploy.
   - The rest keep theirs so that the move stays a move.
   - Durable consumer names keep their `admin-api-*` prefixes: a new name would orphan the old
     durable's backlog.

3. **Its own Vault identity.**
   - `dcms-jobs-worker` reads `secret/dcms/jobs-worker`.
   - `infra/vault/apply.sh` mirrors admin-api's keys there on every deploy. It skips
     `Platform__DbPassword`, writes only values that differ, and passes them over stdin.
   - The policy grants the transit keys that background work uses, and not the tenant-secret or
     TLS keys, which only admin-api's endpoints use.

4. **Plugins' background work only in jobs-worker.**
   - content-api and admin-api pass `runsBackgroundWork: false`, so this holds by construction
     rather than by each plugin's own plane check.
   - The site gate reconciler gained an advisory lock; it had none, so every worker replica
     rescanned every environment.

5. **Statelessness.** Everything jobs-worker runs is one of:
   - a shared durable consumer;
   - a `SKIP LOCKED` or lease claim;
   - an advisory-locked pass;
   - the audit chain's single writer (`FOR UPDATE`; replicas take turns).

   More replicas is more throughput. The exception is the audit chain writer, which stays serial
   by design.

6. **Visibility (epic #650, JW5).**
   - **Recurring ledger jobs** (`RecurringJobs.AddRecurringJob`). A scheduler enqueues one routine
     job per slot of its period. The ledger's unique `(TenantId, DedupeKey)` makes that once per
     slot across replicas, so there is no lock.
     - Platform lane: `audit.maintenance` (1 h), `analytics.retention` (6 h),
       `notifications.retention` and `ai.retention` (24 h).
     - `tenancy.usage-sweep` (1 h) queues one tenant-lane `tenancy.usage` job per due tenant,
       visible in that tenant's jobs.
   - **Plugin interval jobs** are ledger jobs (`plugin:{id}:{job}`, routine, deduped per slot), so
     a tenant sees its plugins' schedules. Retries follow the ledger: 3 attempts, not 5 NATS
     deliveries.
   - **Continuous drains** get no ledger row per item, which would be thousands a day. These are
     the outboxes, event consumers, the frequent sweeps (scheduled publish, invitations,
     connections, certificate notifications), AutomationWorker and RunWorker. Instead the
     platform console's queue overview lists every durable consumer of the event streams with its
     lag, and the two outboxes' backlogs.
   - **`JobLiveUpdates`** is a shared durable consumer. A group send goes over the SignalR
     backplane to every replica's sockets, so one ordered consumer per replica had pushed every
     change once per replica.

## Consequences

- **admin-api runs no background work at all.** A pass that misbehaves can no longer fail
  console requests.
- **jobs-worker down means all background work stops**, where admin-api used to be an accidental
  second copy. It is health-gated in the deploy and restarts on failure; scale it to two
  replicas when one is not enough.
- **Memory ceilings moved:** admin-api 768m → 640m, jobs-worker 512m → 768m. The Postgres pools
  are unchanged (25 / 10).
- **One more image** in CI (17).
- **Integration test fixtures** start a jobs-worker host next to admin-api on the same
  containers. `ServiceBoundaryTests` assert:
  - admin-api runs no background hosted service except the migrator and the socket push;
  - jobs-worker runs all of them and maps only health.
