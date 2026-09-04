# Runbook

## Local development

Prereqs: .NET 10 SDK, Node 24 + pnpm, Docker Desktop.

```sh
docker compose up -d --build   # infra + all services
./scripts/smoke.ps1            # asserts health + provisioning
pnpm dev:admin                 # admin SPA with hot reload on :5173
```

Dev ports: admin SPA 5000 (container) / 5173 (vite), identity 5001 (and 8080 for
the OIDC issuer alias), admin-api 5002, content-api 5003, media-worker 5004,
site-builder 5005, site-host 5006, ai-gateway 5007, platform-api 5008,
platform-spa 5010, Postgres 5432, Redis 6379,
NATS 4222 (monitor 8222), MinIO 9000 (console 9001), Vault 8200, Mailpit 8025
(SMTP 1025). email-worker and Forgejo publish no host port. Telemetry ports are
listed in [`infra/observability/README.md`](../infra/observability/README.md).

Dev credentials (compose only): Postgres `dcms`/`dcms-dev`, MinIO
`dcms`/`dcms-dev-secret`, Vault root token `dcms-dev-root`. Seeded platform
admin: `admin@dcms.local` / `Admin!23456` (override via
`Identity__SuperAdmin__Email` / `__Password`).

### Auth / OIDC (Phase 2)

The identity service (OpenIddict on ASP.NET Core Identity) issues signed JWT
access tokens. Resource servers (admin-api audience `dcms-admin-api`,
ai-gateway audience `dcms-ai-gateway`) validate them with JwtBearer against the
discovery document. The admin SPA uses authorization code + PKCE
(client `dcms-admin-spa`); admin-api → ai-gateway uses client credentials
(client `dcms-admin-api`, scope `dcms.ai`).

**Issuer hostname.** Browser and backend containers must reach identity at the
*same* URL so the token issuer and `jwks_uri` resolve for both. Compose uses the
network alias **`dcms-identity:8080`**. For SPA login to work end to end under
compose, add to your hosts file:

```
127.0.0.1 dcms-identity
```

Then the SPA (served at :5000) authenticates against `http://dcms-identity:8080`
and the backend services reach the same host via internal DNS. For pure
`dotnet run` local dev (no compose) everything is on `localhost` and no hosts
entry is needed (identity on :5001, SPA dev server on :5173).

### Tenancy (Phase 3)

admin-api owns the `tenancy` schema (tenants, domains, memberships, roles,
permissions, invitations) and applies its migration on startup
(`Tenancy:Migrate`). The current tenant is selected per request via the
`X-Dcms-Tenant` header (tenant uuid); content-api resolves tenants by Host.

Key endpoints (all under `/api/admin`, JWT-protected): `POST /tenants`
(SuperAdmin; optional `ownerUserId`/`ownerEmail`), `GET /tenants`,
`GET /me/tenants`, `GET|POST /roles`, `GET /members`,
`POST /members/{id}/roles`, `POST /invitations` + `POST /invitations/accept`,
`GET|POST /domains` + `POST /domains/{id}/verify`. Permission checks use
`RequirePermission(...)`: effective permissions are resolved from
`tenant_role_permissions`, cached in Redis (`perm:{tenantId}:{userId}`, 5 min),
invalidated directly on role change and via the `membership.changed` event.
SuperAdmins bypass tenant permission checks.

The header is a *request*, not a claim — `TenantStore` resolves it by identifier and
cannot know who is asking. `TenantMembershipMiddleware` therefore sits between
`UseMultiTenant` and `UseAuthorization` and 403s any authenticated caller who names a
tenant they are not a member of, so an endpoint guarded by a bare
`RequireAuthorization()` is not cross-tenant by default. SuperAdmins pass; the one
exemption is `POST /invitations/accept`, marked `AllowNonMemberTenant` because
becoming a member is the point of the call (it reads the tenant from the invitation
token, never from the header). Domain verification reads a TXT
record `_dcms-verify.{hostname}`; set `Domains:AutoVerify=true` in dev to skip DNS.

### Plugins & CMS (Phase 4)

admin-api owns the `plugins` + `cms` schemas (migrated on startup alongside
tenancy). Plugin instances: `GET /api/admin/plugins/catalog` (manifests),
`GET/POST/PUT /api/admin/plugins/instances`, `POST .../{id}/{enable|disable}` —
config is validated against the manifest's JSON Schema (JsonSchema.Net). CMS
authoring: `GET/POST /api/admin/content`, `PUT /api/admin/content/{id}` (each
save is a new immutable version), `POST .../{id}/publish|unpublish`. Publishing
pins `PublishedVersionId` and writes a `content.published` outbox row in the same
transaction; the OutboxDispatcher relays it to NATS.

Delivery (content-api): `GET /api/{instanceSlug}/{contentType}[/{itemSlug}]`
serves published content, resolving the tenant from `X-Dcms-Tenant` (Phase 4) /
Host (Phase 8) and the instance by slug; plugins opt in via
`MapContentList`/`MapContentGetBySlug` (recorded into the PluginRouteTable at
startup). Reads are Redis-cached (`t:{tenant}:c:{instance}:…`) with a per-instance
generation counter; the ContentCacheInvalidator consumes `content.published`/
`content.unpublished` to delete the item key and bump the counter.

### Media (Phase 5)

admin-api `POST /api/admin/media` (multipart, ≤50 MB): content-sniffs by magic
bytes, re-encodes images through ImageSharp (strips EXIF/IPTC/XMP), stores the
original at `dcms-media/tenants/{tenantId}/{assetId}/original.{ext}`, writes a
`media_assets` row and dispatches `media.process.image`. media-worker consumes
it, generates the webp ladder (320/640/1280/1920 + thumb, skipping widths above
the source), stores variants, writes `media_variants`, marks the asset Ready and
emits `media.processed`. content-api serves bytes at
`GET /api/media/{assetId}/{variant}` (variant = `original` or a kind like
`webp-640`) proxied from MinIO with immutable cache headers; `IMediaResolver`
turns an asset id into its variant URLs for plugins. Image processing
(sanitizer, webp ladder, sniffer) is covered by in-process unit tests; the full
upload→worker→serve round-trip is a Testcontainers integration test.

### Video & audio (Phase 6)

media-worker also consumes `media.process.video` and `media.process.audio`.
Video → FFMpegCore/ffmpeg HLS ladder (1080/720/480, H.264+AAC, 6s segments,
skipping heights above the source) with a hand-written `master.m3u8` + a poster
frame; the whole tree is uploaded under the asset's `hls/` prefix and the
playlists + poster are recorded as variants (`hls-master`, `hls-720`, …,
`poster`). Audio → AAC `audio.m4a` + a downsampled `peaks.json` waveform, with
duration stored in the asset metadata. content-api serves HLS at
`GET /api/media/{assetId}/hls/{file}` (m3u8 `max-age=60`, segments immutable,
Range supported); audio variants stream via the existing variant endpoint.
Plugins: VideoGallery (`video`), VideoStreaming (`stream`), AudioLibrary
(`track`). HLS serving (incl. Range 206) is a Testcontainers test; the ffmpeg
transcode test self-skips unless a full ffmpeg (with libx264) is on PATH.

### OpenAPI + scheduled publishing (Phase 7)

content-api serves the per-tenant spec assembled from enabled plugin instances:
`GET /api/openapi.json` / `.yaml` (Redis-cached, ETag = config hash, supports
`If-None-Match` → 304) and a Scalar viewer at `GET /api/openapi`. Each operation
description embeds the admin-authored instance description so AI tools see the
intent of each instance. The cache key includes a hash of instance
slugs/configs/versions, so config changes self-invalidate.

Scheduled publishing: `POST /api/admin/content/{id}/schedule {publishAt}` queues
a `scheduled_publishes` row for the current draft version. `ScheduledPublishWorker`
polls (`Scheduler:PollSeconds`, default 15) and claims due rows with
`FOR UPDATE SKIP LOCKED` inside a transaction, publishing each exactly once
across replicas via the same content.published outbox path.

### Site hosting — Mode A (Phase 8)

admin-api owns the `sites` schema (sites + site_builds). Site editing:
`GET/POST /api/admin/sites`, `PUT /api/admin/sites/{id}/definition` (component
tree JSON), `POST /api/admin/sites/{id}/publish` (snapshots the definition into
a build and emits `site.publish.requested`). Domains link to a site via
`POST /api/admin/domains/{id}/site`.

The publish job is routed by render mode — `site.publish.requested.staticfiles`,
`.staticprerender` or `.reactapp` — and site-builder drains those on **separate consumers**:
`site-builder-git` takes Modes A and B, `site-builder-static` takes Mode C. They cost two
orders of magnitude apart (measured on vps1: ~340 ms, ~1 s and ~30 s), and JetStream delivers
in order, so on one consumer a Mode C publish behind a running Mode B build had a p95 of
**34.8 seconds** while its minimum stayed at 338 ms. Which lane is backed up is the "Queue
depth by lane" panel on the Site builds dashboard; `nats consumer report SITES` says the same
thing from the command line. `DCMS_BUILD_CONCURRENCY` sizes the git lane (default 1 — a Mode B
sandbox holds two of four cores) and `DCMS_BUILD_CONCURRENCY_STATIC` the static one
(default 2). Lane membership lives in `src/Services/Dcms.SiteBuilder/SiteBuildLane.cs`.

site-builder consumes the publish job and
prerenders the component tree to static HTML **in C#** (`SiteRenderer` — layout
primitives render directly, data-bound/plugin components become hydration
placeholders; no Node dependency), uploads artifacts to
`dcms-sites/{tenant}/{site}/{build}/`, sets the active build and emits
`site.published`. site-host resolves the request Host → verified+linked domain →
active build, serves artifacts from MinIO (SPA fallback to index.html, in-memory
route cache invalidated on `site.published`), and reverse-proxies `/api` + `/hub`
to content-api (YARP `IHttpForwarder`) injecting `X-Dcms-Tenant`. Mode B
(generated React app) is wired in Phase 10. The renderer is covered by
in-process unit tests; the full publish→render→serve-by-domain path is a
Testcontainers test.

### Website editor (Phase 9)

`packages/editor-core` holds the component-tree schema plus immutable tree ops
(insert/remove/move/update, cycle-safe) and snapshot undo/redo (vitest-covered).
`packages/site-components` is the component registry (layout primitives + plugin
components with prop JSON schemas, `requiredPluginId`, and binding contracts).
The admin SPA editor (`apps/admin/src/editor`) wraps a `zustand` store over
editor-core: a `@dnd-kit` palette (drag-to-add + click-to-add, filtered to the
tenant's enabled plugins), a selectable canvas, a prop inspector generated from
each component's schema, a data-source binding picker (plugin instance), page
switcher, undo/redo, and Save (`PUT .../definition`) / Publish
(`POST .../publish`). Run `pnpm --filter @dcms/editor-core test` for the
editor-core unit tests.

### AI gateway + generation + Mode B (Phase 10)

ai-gateway exposes an internal `POST /v1/chat` (dcms.ai client-credentials).
`IChatProvider` has two implementations: `AnthropicProvider` (official `Anthropic`
SDK) and `OpenAiCompatibleProvider` (the `OpenAI` SDK with a base URL — covers
OpenAI, Ollama, LM Studio). `AiProviderResolver` picks the provider per tenant
from `ai.tenant_ai_settings`, decrypting the tenant API key via Vault Transit at
call time, falling back to the global defaults in Vault KV
(`secret/dcms/ai-gateway` → `Ai:Defaults:*`). Tenant AI config is managed at
`GET/PUT /api/admin/ai/settings` (the key is encrypted on write via Transit and
never returned). AI editor generation: `POST /api/admin/ai/generate/component`
and `.../site` assemble a prompt from the component contract + the tenant's
enabled plugin instances, relay it to ai-gateway, and return validated JSON.
Mode B sites: site-builder's `ReactAppBuilder` materializes the AI/editor file
map and builds it. A Mode B project is untrusted code, so install + build run in
an ephemeral, per-build **sandbox container** (see below), never in the builder
process. Install runs `--ignore-scripts` (no package lifecycle code executes) and
may fetch dependencies from the registry; the build (`vite build`, which executes
site `vite.config`) runs with **no network** and a scrubbed environment, then
`dist/` is uploaded. The OpenAI-compatible provider is covered by an in-process
stub test; Transit and live model calls run against real infra/keys.

#### Mode B build sandbox (security)

Builds are isolated so a malicious site cannot reach platform secrets or other
tenants (see [[git-integration-progress]]):

- **Isolation:** each build phase runs as `docker run --rm` on the host daemon via
  a `docker-socket-proxy` (the builder never mounts the raw socket), with
  `--cap-drop ALL`, `--security-opt no-new-privileges`, `--read-only`, per-build
  cpu/memory/pids limits, and — for the build phase — `--network none`. Set
  `DCMS_BUILD_RUNTIME=runsc` in `.env` after installing gVisor on the host
  (`/etc/docker/daemon.json`) for kernel-level isolation.
- **Env scrub:** the build process inherits *no* platform env — `ReactAppBuilder`
  clears `ProcessStartInfo.Environment` and passes only PATH/HOME/CI/npm_config_*.
- **Least privilege:** site-builder no longer gets `*service-env`/`*prod-env`. It
  uses a Postgres role scoped to the `sites` schema (`dcms_sitebuilder`,
  `infra/postgres/init/02-service-roles.sh`) and a MinIO service account scoped to
  the site buckets (`dcms-sitebuilder`, `infra/minio/init.sh`), and holds **no**
  Vault token.

**Deploy steps (in addition to the normal three-file `up`):**

1. Build the sandbox image so the daemon can launch it:
   `docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml --profile sandbox build site-build-sandbox`
2. Create the host work dir (world-writable so the builder and sandbox uid can both
   write): `mkdir -p ~/dcms-data/build-work && chmod 0777 ~/dcms-data/build-work`.
3. Set a strong `FORGEJO_WEBHOOK_SECRET` in `.env` (admin-api now refuses to start
   in Production with an empty/default one); re-register site webhooks afterwards.
4. Optionally set `SITEBUILDER_DB_PASSWORD` / `SITEBUILDER_MINIO_USER` /
   `SITEBUILDER_MINIO_PASSWORD` in `.env`.

**Existing cluster note:** the scoped Postgres role and MinIO account are created by
the init jobs only on a *fresh* data dir. On the already-provisioned vps1, run the
statements in `02-service-roles.sh` against Postgres once, and
`mc admin user add` + `mc admin policy attach` (per `infra/minio/init.sh`) against
MinIO, then redeploy. Until then, temporarily point site-builder back at the
existing creds — but the sandbox + env-scrub already prevent secret exposure.

### Search, Analytics & Visitor Auth (Phase 11)

**Search** — `search` schema with a Postgres generated `tsvector` (GIN) + a
pg_trgm GIN index on title (Npgsql `HasGeneratedTsVectorColumn`). content-api's
`SearchIndexer` consumes `content.published`/`unpublished` and projects
searchable content types (title + text fields) into `search_documents`;
`GET /api/{slug}/search?q=` runs `websearch_to_tsquery` ranked search, tenant-scoped.

**Analytics** — `analytics` schema (events + daily_rollups). content-api
`POST /api/{slug}/collect` (anonymous beacon) publishes `analytics.events`;
admin-api's `AnalyticsConsumer` persists events and increments per-day rollups;
`GET /api/admin/analytics?days=` serves the dashboard (`analytics:read`).
Event-table range partitioning is a documented later optimization.

**Visitor Auth** — `visitors` schema (visitor_accounts + visitor_refresh_tokens),
a separate per-tenant identity pool. content-api endpoints
`POST /api/{slug}/{register|login|refresh}` + `GET /api/{slug}/me` use
`PasswordHasher` and `VisitorTokenService` (HMAC JWTs with audience
`dcms.site:{tenantId}` — a token for one tenant fails at another's endpoint;
`Visitor:SigningKey` from Vault in prod). Other plugins gate content with
`VisitorAuthEndpoints.AuthenticateAsync`. The token service is unit-tested
(issue/validate, cross-tenant rejection, tamper rejection).

### Live chat (Phase 12)

The LiveChat plugin runs a SignalR hub in content-api at `/hub/chat` with a Redis
backplane (cross-replica fan-out). Connect with `?tenant={slug}`; agents
additionally pass a platform JWT via `access_token` and are recognised as agents
only if they are members of that tenant (checked against the `tenancy` schema —
see ADR 0004). Visitor methods: `StartConversation`, `JoinConversation`,
`SendMessage`; agent methods: `JoinConversation`, `SendAgentMessage`,
`CloseConversation`. Client events: `ReceiveMessage`, `ConversationStarted`,
`ConversationActivity`, `ConversationClosed`. Every message also publishes
`chat.message.posted` to the NATS **CHAT** stream; admin-api's
`ChatFanoutConsumer` is the durable out-of-band fan-out point (offline-agent
notifications — documented extension). The agent console (admin SPA `/chat`)
lists/loads conversations from admin-api (`GET /api/admin/chat/conversations[/{id}/messages]`,
`chat:read`) and connects to the hub for realtime. The visitor widget is the
embeddable `createChatWidget({ tenant })` from `@dcms/site-components` (vanilla
TS, reaches the hub through site-host's `/hub` proxy).

### Hardening (Phase 12)

- **RLS** — `RlsConfigurator` enables a `tenant_isolation` policy (keyed on the
  `app.tenant_id` GUC) on every tenant-scoped table after migrations, as
  defense-in-depth on top of the EF query filters. The app connects as the table
  owner and is unaffected; the least-privilege `dcms_rls` role (no BYPASSRLS) is
  what the isolation test uses to prove the policy. Gate with `Tenancy:ApplyRls`.
  See ADR 0005.
- **Rate limiting** — content-api applies a per-client (IP) fixed-window global
  limiter (`RateLimiting:PermitLimit`/`WindowSeconds`, default 600/60 s); `/health`
  and `/hub` are exempt. 429 on exceed.
- **Security headers** — content-api and site-host emit `X-Content-Type-Options`,
  `X-Frame-Options: DENY`, `Referrer-Policy`, and an optional
  `Security:ContentSecurityPolicy`.
- **CORS** — content-api allows the admin SPA origins (`Cors:AllowedOrigins`,
  default localhost:5173/5000) with credentials, required for the cross-origin
  agent hub connection.

### Audit log

- **Write path** — every service with database access records into
  `audit.audit_outbox` *inside the transaction that commits the change being
  recorded*, so a committed change cannot exist without its audit record.
  `AuditChainWriter` (admin-api) drains the outbox, appends to
  `audit.audit_events` with an HMAC chain, then fans out to the `AUDIT` NATS
  stream for external sinks. Only email-worker (no database) and site-builder
  (confined to the `sites` schema) publish to NATS directly.

- **`Audit__ChainKey` is required outside development.** A ≥32-byte base64 key,
  needed by all six services that own the audit schema (admin-api, identity,
  content-api, ai-gateway, media-worker, site-host). They refuse to start without
  it in non-Development environments, because a chain nobody can verify is
  indistinguishable from a tampered one. Development falls back to a fixed
  built-in key and logs a warning — the chain is **not** tamper-evident there.

  **On vps1 it is an environment variable, not a Vault secret.** Vault there is
  Shamir-sealed and supplementary — the real secrets already come from the
  compose `prod-env` environment — so the key lives in `~/baas-dcms/.env` as
  `AUDIT_CHAIN_KEY` and reaches the containers through the `x-prod-env` anchor in
  `docker-compose.prod.yml`. Vault is the better home for it once the cluster is
  routinely unsealed; until then, `.env` is what actually works.

  ```sh
  # vps1 — generate once, on the server, and never print it
  printf '\nAUDIT_CHAIN_KEY=%s\n' "$(openssl rand -base64 48)" >> ~/baas-dcms/.env

  # Vault-managed installations
  vault kv patch secret/dcms/admin-api Audit__ChainKey="<value>"
  ```

  **Never rotate this key.** Every record written under the old key stops
  verifying the moment it changes, and a chain that fails verification is
  indistinguishable from one that was tampered with. It is append-only
  configuration: back it up with the rest of `.env`, and if it is ever lost,
  the honest response is to record that history before date X is no longer
  verifiable — not to generate a new key and pretend otherwise.

- **What the integrity guarantee is.** `POSTGRES_USER: dcms` is a cluster
  superuser and seven of eight services connect as it, so the append-only
  trigger and the `REVOKE UPDATE, DELETE` can be turned off by anyone holding
  the app's credentials. They stop accidents, not attackers. What an attacker
  with full database write access *cannot* do is recompute the HMAC without the
  Vault-held key — that, plus off-box anchors, is the real control. Verify from
  the SPA (Audit log → Verify chain) or `GET /api/admin/audit/verify`.

- **The schema is created by the deploy, not by hand.** `infra/postgres/init/*`
  runs only on an empty data directory, so this used to need a manual
  `CREATE SCHEMA audit` plus grants on any cluster older than the feature. The
  `postgres-bootstrap` job re-applies those scripts against the running cluster
  on every deploy — they are all idempotent — and `scripts/deploy.sh` runs it
  first of the three migration jobs. The tables, partitions and trigger then come
  from `TenancyMigrator` + `AuditSchemaConfigurator`.

- **The `AUDIT` stream's policy is create-only.** `provision-streams.sh`
  converges an existing stream's *subject list* but never its retention, max-age
  or discard policy: for AUDIT those are compliance properties, and changing
  retention means recreating the stream and discarding its backlog. AUDIT must be
  `limits` retention with `--discard new`; a stream created with work-queue
  retention would allow only one consumer and drop messages on ack. Provisioning
  prints a warning when it finds a mismatch — fix it with `nats stream edit AUDIT`.

- **Two audit subjects, opposite directions.** `audit.submitted` is *inbound* —
  email-worker and site-builder publish there because they cannot reach the
  schema, and `AuditIngestConsumer` in admin-api drains it into the outbox so
  everything reaches the chain by one path. `audit.recorded` is *outbound*
  fan-out, published after a record is chained, for sinks outside the platform.
  Keeping them apart is load-bearing: a writer consuming its own fan-out would
  chain every record twice.

- **Attribution crosses process boundaries as NATS headers**
  (`Dcms-Correlation-Id`, `Dcms-Actor-*`, `Dcms-Tenant`, `Dcms-Causation-Id`).
  None of the event records changed to carry them, and a consumer that ignores
  them behaves as before. Anything restored from them is stamped
  `attribution=propagated`, because a peer's assertion is not an
  authentication — the audit page labels it, and it should stay labelled.

  The two database outboxes bridge the same gap in time rather than space: both
  carry a `ContextJson` column populated at enqueue and restored before publish.
  Without it the chain breaks at the dispatcher's two-second poll, and "the site
  build attributes back to the human who clicked publish" stops working — that
  path runs request → `content_outbox` → dispatcher → JetStream → site-builder.

- **The git webhook is `ActorKind.Webhook`, `attribution=inferred`.** The HMAC
  proves the push came from Forgejo; it says nothing about who pushed. The
  payload's git author is a self-asserted string and is deliberately not treated
  as an identity. A failed HMAC records `security.webhook.rejected`.

- **Existing tenants get `audit:read` on startup.** `OwnerPermissionBackfill`
  grants each tenant's system Owner role every key in `PlatformPermissions.All`
  it is missing. Without it a permission added after a tenant was created reaches
  new tenants only, so the audit page would ship to an installation where nobody
  can open it and no error says why. Idempotent, additive only — a role someone
  narrowed by hand is a decision, not drift — and it records what it granted.

- **Export is itself audited, before a byte is written.** `audit.exported`
  carries the filter and the row cap; the response is a stream, so recording it
  afterwards would mean recording it after the data had already left. NDJSON, not
  CSV: a truncated download stays a valid prefix, and CSV would have to flatten
  the diff and the metadata, which is most of what an export is for. Needs
  `audit:export`, which is separate from `audit:read` on purpose.

- **A record another tenant owns answers 404, not 403.** "That id exists but is
  not yours" is itself a disclosure. Platform-scope records (sign-ins) resolve
  through membership and come back with a restricted whitelist projection — no
  resource, no HTTP detail, no metadata — so tenant A cannot learn which other
  workspaces a shared account touched.

- **Never delete audit rows.** Tenant purge deliberately excludes the `audit`
  schema; an integration test asserts it. Retention drops whole monthly
  partitions instead, and each month is a self-contained chain so dropping one
  breaks nothing.

- **Field values are default-deny.** `AuditRedactionDefaults` is an explicit list
  of the entity types whose values may appear in a record. A type not on it still
  produces a record — action, actor, table, which fields changed — but with the
  values withheld. Add a table or a column and it discloses nothing until someone
  has read the list and decided otherwise. Three further nets inside an opted-in
  type: a per-type deny list, `[AuditSensitive]`, and a name heuristic
  (`password`, `secret`, `token`, `hash`, `apikey`, `ciphertext`, `credential`,
  `private`, `salt`, `signature`). Records carry the `RedactionVersion` that
  produced them, so a blank field years from now is still interpretable.

- **A missing `UseDcmsAuditInterceptors(sp)` is silent.** Registering the
  interceptors in DI is not enough — EF Core does not resolve interceptors from
  the application container by itself. A new `DbContext` whose `AddDbContext`
  omits that call will commit changes and record none of them, without an error.
  `AuditInterceptorDiscoveryTests` saves through every context and asserts the
  buffer drained; add the new context to its list.

- **Set-based statements record shape, not content.** `ExecuteUpdate` and
  `ExecuteDelete` never load the rows they change, so there is no before-image to
  record and there will not be one — reading first would double every delete on
  the platform to serve the log. The interceptor records the table, the statement
  shape and the affected row count. Where that is too thin, the call site wraps
  the block in `scope.SuppressBulkCapture()` and records something better: the
  tenant purge writes one manifest of per-table counts, a role update reads the
  old permission set before replacing it. `AuditBulkStatementCoverageTests` fails
  the build if a file issues such a statement without mentioning `IAuditRecorder`
  or `AuditScope`, so the decision cannot be skipped by accident.

- **The purge writes its intent before it acts.** `tenant.purge.started` goes
  through `RecordNowAsync`, which propagates a sink failure — a purge that cannot
  be recorded does not happen. `tenant.purged` follows with the count manifest. A
  start with no matching finish means a purge died partway through, and it is the
  only way to find that out: the rows that would have shown it are the ones that
  were deleted.

#### Sealing, retention and alerting

- **`AuditMaintenanceWorker` runs hourly in admin-api, and once at startup.** Its
  pass is: create partitions ahead of the writer → seal finished months → drop
  expired partitions → publish metrics → check for omissions. A job that fires
  once a month is a job nobody notices has stopped, and one that has to run at
  midnight on the first misses a month whenever a deploy lands badly. Every step
  is idempotent, so running it sixty times too often costs sixty no-ops.

- **The chain's unique index is per-partition, never on the parent.** Postgres
  requires a unique index on a partitioned table to include the partition key,
  and adding `OccurredAt` to `("ChainKey","Period","Seq")` would defeat the
  constraint — two rows could then claim one `Seq` at different instants. So each
  partition carries its own `UX_audit_events_<month>_chain`, created by
  `AuditSchemaConfigurator.EnsureChainIndexesAsync`, which runs at startup and in
  every hourly maintenance pass over *all* partitions. The practical consequence:
  **a partition created by hand — backfilling an old month, say — has no chain
  index until that method next runs.** Writes still serialise on the chain-head
  row lock, so the index is a backstop rather than the primary control, but do
  not leave a partition without it. Re-running it is one idempotent statement.

- **Sealing happens before dropping, always — that order *is* the safety
  property.** `AuditChainSealer` verifies a finished month, then writes an
  `audit.chain_anchors` row holding the sequence range, the row count, the first
  and last hash, and an HMAC over all of it. That anchor is what lets a month
  whose rows are gone still be shown to have been intact. Dropping first and
  anchoring never would turn retention into evidence destruction.

- **A month that will not seal is never dropped.** A segment refuses to seal
  precisely when its chain does not verify, so the partition that most needs
  looking at is the one retention leaves alone. Look for
  `Refusing to seal audit segment …` at `Critical`, and
  `Keeping audit partition for … : N segment(s) are not sealed.` at `Warning`.

- **Anchors are logged as well as stored, at `Critical`.** The line
  `AUDIT ANCHOR {chain}/{period} seq A-B rows N hash … hmac …` is not an error;
  the level is a routing instruction, so the line leaves Postgres for stdout and
  whatever collects it. **An anchor that exists only in the database it attests
  to is not an off-box anchor** — if these lines are not being shipped somewhere
  outside the cluster, the third integrity control is not actually in place.

- **Anchors are never dropped**, by retention or by tenant purge.
  `PartitionDroppedAt` is stamped on the anchor when its rows go, so a gap in the
  months is visible as a gap rather than as an absence.

- **Retention is `Audit:RetentionDays`, default 400.** Rows are removed only by
  `ALTER TABLE … DETACH PARTITION` followed by `DROP TABLE`. The drop is itself
  recorded as `audit.partition.dropped`.

- **`ProducerSeq` gaps are the check the chain cannot perform.** A chain over
  records that were silently dropped verifies perfectly — deleting the tail of a
  chain leaves a valid chain. Each process stamps a `ServiceInstance` and a dense
  counter, and `AuditGapDetector` compares the counter's range against the rows
  that arrived. The hourly check looks back 6 hours and stops 5 minutes short of
  now, because a process that has taken a sequence number but not yet flushed
  looks momentarily like a gap at its own tail; the windows overlap so nothing is
  skipped. A gap reads:
  `Audit gap: {service} instance {id} issued sequence A-B but only N record(s) arrived`.

- **Metrics, meter `Dcms.Audit`,** exported through the existing OpenTelemetry
  wiring when `OTEL_EXPORTER_OTLP_ENDPOINT` is set:

  | Metric | Alert when |
  |---|---|
  | `dcms.audit.recorded` | it **flat-lines** during known traffic — a silent audit log looks exactly like an idle platform, which is why the absence is the alarm rather than a spike |
  | `dcms.audit.sink_failed` | > 0 — each one now survives only in a `Critical` log line |
  | `dcms.audit.chain_broken` | > 0, ever — this is an incident, not a threshold |
  | `dcms.audit.producer_gap` | > 0 outside the 5-minute settle window |
  | `dcms.audit.outbox_depth` | sustained growth: the writer has stopped |
  | `dcms.audit.writer_lag` | > 15 min — the worker already logs `Critical` at that point |

- **Verifying by hand:** `GET /api/admin/audit/verify` for the current tenant, or
  the SPA's *Audit log → Verify chain*. Every failing exit of the verifier
  increments `dcms.audit.chain_broken`, so a finding cannot be discovered on one
  operator's screen and nowhere else.

### Load and stress testing

`loadtest/` is the harness: eight scenarios covering the delivery plane, hosted
sites, the admin plane, the publish pipeline, media processing and site builds,
pointed at an environment by profile and leaving an evidence bundle behind.
See [`loadtest/README.md`](../loadtest/README.md) — it carries the procedure for
reading a bundle and the list of open bottleneck hypotheses.

```sh
export DCMS_LOADTEST_USER=… DCMS_LOADTEST_PASSWORD=…      # a platform SuperAdmin
node loadtest/seed/provision.mjs --env vps1
./loadtest/run.sh --env vps1 --scenario delivery --vus 10 --duration 2m
node loadtest/seed/teardown.mjs --env vps1
```

Two things to know before the first run:

- **content-api's per-IP rate limit is 600 req / 60 s**, in-process, so any run
  above ~10 req/s from one source measures the limiter rather than the platform.
  `RATE_LIMIT_PERMITS` raises it for a measurement window; put it back afterwards
  and re-assert it with the `ratelimit` scenario.
- **Access tokens live ten minutes**, so `run.sh` refuses an authenticated run
  longer than eight. `delivery`, `sitehost` and `ratelimit` need no token and are
  the ones to soak with.

(This replaces the Phase 12 `scripts/load-smoke.js` smoke, which covered two
cached content-api reads; the `delivery` scenario is its superset.)

## In-app notifications

The bell in the admin SPA. Notifications are raised by durable consumers in admin-api
that translate already-published events, so most producers were untouched. See
[ADR 0009](adr/0009-in-app-notifications.md).

- **Nothing to do by hand.** The `notifications` schema and its `dcms_rls` grants are in
  `infra/postgres/init/*`, which `postgres-bootstrap` re-applies on every deploy; the
  tables come from `TenancyMigrator`; the `NOTIFY` stream comes from `nats-init`. All
  three run before any service is rolled, and a failure stops the deploy. Push to
  `master` is the whole procedure.

  (The `dcms_rls` grant is the part that looks optional and is not. The app connects as
  the table owner, so skipping it breaks nothing visible — it breaks the RLS isolation
  test, which is the thing that proves one tenant cannot read another's notifications.)

- **The `NOTIFY` stream** carries `notify.>` with `limits` retention. Provisioning
  converges subjects on an existing stream but not retention, so if this one somehow
  exists as a work queue it warns and you fix it with `nats stream edit NOTIFY` — a work
  queue would permit only one consumer per subject filter.

- **The hub is at `/api/hub/notifications`, not `/hub/...`.** That is deliberate: it
  rides the existing `/api/*` route to admin-api, so **no Caddyfile change was needed**
  and the edge did not have to be restarted. `/hub/*` on the admin host still goes to
  content-api for chat. Both are exempt from rate limiting.

- **The backplane channel prefix is `dcms-notify`.** content-api's chat hub uses
  `dcms-chat` on the same Redis. If notifications ever appear in the chat console or
  vice versa, these have collided.

- **No Redis connection string means no backplane**, silently, exactly as for chat. With
  more than one admin-api replica that shows up as "some admins get the toast, some do
  not" — the consumer wrote the row on one replica and the push never left it. The bell
  still fills in on the next fetch, which is what makes this easy to miss.

- **Nothing appearing at all?** In order: is the `NOTIFY`/`SITES_EVENTS`/`MEDIA_EVENTS`
  stream present (`nats stream ls`); are the durables consuming
  (`nats consumer report SITES_EVENTS` — look for `admin-api-notify-*`); and does the
  tenant have anyone holding the gating permission? The last one is not an error and is
  logged at Debug: a notification with no audience is dropped, by design.

- **Seeing the same notification several times?** It is almost certainly not the
  notification layer. The dedupe key names the *fact* (`site.published:{buildId}`), so
  duplicates mean the fact genuinely happened more than once — a producer whose job
  outlived its JetStream `AckWait` and was redelivered mid-flight, doing the work again.
  Check `nats consumer info <STREAM> <durable>`: if `delivered.consumer_seq` runs well
  ahead of `delivered.stream_seq`, that consumer is redelivering. `ack_wait` is in
  nanoseconds; the JetStream default of 30 s is far too short for a site build or a video
  transcode. The fix is `AckWait` plus an `AckHeartbeat` around the work, not a longer
  deadline alone — see `Dcms.Shared.Messaging/AckHeartbeat.cs`.

- **A burst of stale notifications right after deploying a new consumer** is the
  `*_EVENTS` backlog: those streams keep seven days and a brand-new durable starts at the
  beginning. New consumers are created with `DeliverPolicy.New`, and
  `NotificationConsumerBase.MaxEventAge` (24 h) drops anything older regardless — delivery
  policy is immutable on a durable that already exists, so the age guard is what covers
  the ones already out there.

- **Retention** is 90 days after a notification is read or dismissed
  (`Notifications:RetentionDays`), swept daily under an advisory lock so one replica
  does the work. Unread notifications are never swept.

## Retired JetStream durables

A durable consumer outlives the code that bound it, and an orphaned one holds the stream's
ack floor down forever — the stream keeps every message behind it and grows without bound,
while every outward sign says the deploy went fine.

`provision-streams.sh` ends with a `retire_consumer` list and clears them on every deploy,
so this is no longer a chore anybody has to remember. **When you delete a durable's binder
or convert it to an ephemeral consumer, add a line there** — that is the whole procedure.

The one entry today is `SITES/site-host-cache`: `site-host` used to bind a shared durable
and now uses an ephemeral ordered consumer, so every replica sees every `site.published`
rather than one replica seeing each.

`SITES/site-builder` is queued behind it, commented out. It is the pre-split publish subject's
lane: nothing publishes to `site.publish.requested` any more, but a message written by an
admin-api from before the split still needs a consumer during the rolling deploy that
introduces it — `SITES` is work-queue retention, so a message no filter matches is never
delivered *and* never removed, and the build would sit in Queued until the reaper failed it.
The lane logs a warning for every message it drains, so before uncommenting that line, check
whether it has caught anything in the last release.

## Production profile

Run **without** the dev override, and always with the full overlay set — a
partial `-f` set silently drops overrides and has caused an outage on this host:

```sh
scripts/deploy.sh --check    # validate first; changes nothing
scripts/deploy.sh            # roll and health-gate

# or, by hand:
C="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
$C up -d
```

Differences from dev (`docker-compose.prod.yml`):

- **`edge`** (`Dcms.Edge`, YARP + Kestrel) is the public ingress, owning `:80`/`:443`
  and terminating TLS for the platform hostnames and every tenant custom domain. It
  asks `site-host` `GET /internal/tls-allowed?domain=…` before ordering a certificate,
  so one is issued only for a verified+linked domain. Certificates live in
  `edge.certificates` with the private key encrypted through Vault Transit; ACME email
  via `ACME_EMAIL`, directory pinned per-host (vps1 sets the real one in
  `docker-compose.vps.yml`).
- **Caddy** is still defined and still running, published on loopback only. It exists
  solely so the ports can be handed back without a deploy — see below.
- **Vault** runs in real server mode (`infra/vault/server/config.hcl`, file storage, no
  dev root token). The operator runs `vault operator init` + unseal on first boot
  and provisions the KV/Transit paths + per-service AppRoles (layout mirrors
  `infra/vault/init.sh`). The dev seeding job is a no-op. Services set
  `DCMS_REFUSE_DEV_VAULT=true`, so a service pointed at a dev-mode root token
  **fails fast** instead of running against an insecure backend.
- All secrets come from the host environment (`POSTGRES_PASSWORD`,
  `MINIO_ROOT_USER/PASSWORD`, `VAULT_TOKEN`, `ADMIN_API_CLIENT_SECRET`,
  `ACME_EMAIL`, `PUBLIC_BASE_URL`) — no dev defaults baked in. Services run in the
  `Production` environment with `Auth:RequireHttpsMetadata=true`.

Postgres RLS (ADR 0005) is enabled in every profile as defense-in-depth.

### Handing the ports back to Caddy

The edge is the ingress by default; Caddy is defined, running on loopback, and has
never stopped renewing its own certificates. So the rollback costs nothing and needs
no pipeline round trip — which matters, because a bad ingress is every tenant site at
once. Five lines in `.env`, then `$C up -d caddy edge`:

```sh
EDGE_TLS_ENABLED=false
CADDY_HTTP_PUBLISH=80
CADDY_HTTPS_PUBLISH=443
EDGE_HTTP_PUBLISH=127.0.0.1:8090
EDGE_HTTPS_PUBLISH=127.0.0.1:8453
```

Going back to the edge is deleting them again. The one thing neither direction can undo
is HSTS — leave `EDGE_HSTS_MAX_AGE` at 0 until the edge has been green for a while,
because a browser that has seen the header refuses plain HTTP for the full max-age no
matter which proxy is answering.

**Checking the edge is actually serving.** The Host header is the whole of what the
route table matches, so check with it explicitly:

```sh
curl -si -H 'Host: admin.highgeek.eu'    http://127.0.0.1/api/admin/domains
curl -si -H 'Host: platform.highgeek.eu' http://127.0.0.1/api/platform/observability
openssl s_client -servername admin.highgeek.eu -connect 127.0.0.1:443 </dev/null 2>/dev/null \
  | openssl x509 -noout -issuer -serial -dates
```

`scripts/obs-smoke.sh` additionally asserts `dcms_edge_certificates` has series, which is
how the renewal sweep having stopped is found before the certificates expire rather than
after. `docker compose logs edge` reports every sweep, including one that renewed nothing.

**Certificates on first cutover.** `EDGE_IMPORT_FROM_CADDY` (default `/caddy-data`,
mounted read-only) copies what Caddy already holds into `edge.certificates` instead of
reissuing it. Idempotent and skips everything already known, so it stays on until Caddy
is deleted. If it is ever off, the domains it would have carried are simply issued the
ordinary way — fine for a handful, and a rate-limit problem for a few dozen, because
Let's Encrypt allows ~50 certificates per registered domain per week.

**Deleting Caddy**, once the edge has been green for a week: the `caddy` service in
`docker-compose.prod.yml`, `infra/caddy/`, the `caddy-data`/`caddy-config` volumes, the
edge's `/caddy-data` mount and `Edge__Certificates__ImportFromCaddyPath`, and the
`caddy_http_requests_in_flight` probe in `scripts/obs-smoke.sh`.

## Observability

Full design in [ADR 0008](adr/0008-observability.md); deployment and
troubleshooting in
[`infra/observability/README.md`](../infra/observability/README.md). This section
is the part an operator needs in the middle of an incident.

Grafana is at **https://grafana.highgeek.eu**, **platform SuperAdmin only**. There
is no host port; it is reachable only through the edge.

Grafana no longer speaks OIDC. The edge authenticates against identity, refuses
anyone who is not a SuperAdmin, and passes the result as `X-WEBAUTH-USER` — so an
unauthorized request never reaches Grafana at all, and its access log staying empty
is how you verify the gate. `GF_SECURITY_ADMIN_PASSWORD` is still a break-glass
local login for the case where identity, or the edge, is what is down: the login
form is only reachable when no header arrives, which is exactly that case.

The same arrangement signs operators into **Forgejo**'s web UI, gated on being a
DCMS user rather than a SuperAdmin — Forgejo does its own per-repository
authorization once it knows who is asking.

**Git over HTTPS is deliberately untouched.** `/{owner}/{repo}/info/refs`,
`git-upload-pack`, `git-receive-pack`, LFS and `/api/v1/*` are separate edge routes
with no policy and no header: those requests carry the per-user credential
`ForgejoUserSync` provisions, and asserting a browser session on top of one does not
add a check, it replaces one — a push attributed to whoever is signed in in that
browser. The header injection also refuses outright on any request that already
carries an `Authorization` header, because getting that path list wrong is the
expensive direction.

Two failure modes worth knowing:

- **`EDGE_OIDC_CLIENT_SECRET` unset** → the edge disables its own authentication,
  no route carries a policy, and both consoles fall back to their own sign-in. That
  is the safe direction: this is the public ingress, and refusing to serve tenant
  sites over a missing operator credential would be the wrong trade. `docker compose
  logs edge` says which state it is in at startup.
- **A user with no Forgejo account** reaches Forgejo's own sign-in rather than being
  auto-registered. Forgejo usernames are allocated by the sync with a numeric suffix
  on collision (`rgolias`, `rgolias-2`), so a name the edge invented could claim one
  the sync is about to hand somebody else.

## Platform console (platform.highgeek.eu)

The operations console for platform superadmins. Backed by **platform-api**, which owns
platform authorization and observability and nothing else: users are reached over HTTP from
identity, tenants from admin-api, each behind the service that owns the schema.

### What a deploy needs in `.env`

**No secret for this console lives in `.env`.** Everything sensitive is in Vault and is
generated, not chosen:

```sh
PATH="$(pwd)/infra/vault/bin:$PATH" VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN=<admin> \
  infra/vault/apply.sh --seed     # generates what is absent, never overwrites
```

| Vault path | Key | Read by |
|---|---|---|
| `secret/dcms/platform-api` | `ConnectionStrings__Postgres` | platform-api, to connect |
| `secret/dcms/admin-api` | `Platform__DbPassword` | the migrate job, to `ALTER` the role |
| `secret/dcms/platform-api` | `Observability__LogJanitorSecret` | platform-api, to call the janitor |
| `secret/dcms/log-janitor` | `LOG_JANITOR_SECRET` | the janitor, to check the caller |

The two pairs must agree; `--seed` writes each pair together so they cannot disagree at
creation, and `--check` asserts all four before a deploy rolls anything. Rotating the DB
password is: change both halves, re-run the `migrate` job, restart platform-api.

What stays in `.env` is what is **not** a secret, and what Caddy — which cannot read Vault —
needs to build a site address:

| Variable | Effect if unset |
|---|---|
| `PLATFORM_HOST` | Caddy falls back to `platform.highgeek.eu`; ACME fails until a DNS A record points here. Warned by `deploy.sh`. |
| `PLATFORM_ORIGIN` | Optional. Narrows which origins may frame Grafana; defaults to the platform host plus the localhost dev origins. |
| `LOG_JANITOR_URL` | Container-log truncation stays off, and the console says so. This is the intended default. |

Everything else is automatic: `postgres-bootstrap` creates the role **without a password** (it
runs in a bare postgres image that cannot reach Vault), the `migrate` job sets that password
from Vault and creates the `platform` schema, and `identity-migrate` seeds the
`dcms-platform-spa` OIDC client.

### First deploy to a host: Vault must be provisioned, and the deploy says so

platform-api reads its whole database credential from Vault, so a host with no AppRole for it
has no credential at all — and it **refuses to start** rather than coming up and failing at the
first query. The message names the three commands. Run them once per host:

Three commands on the host, with an admin token supplied for the run and stored nowhere:

```sh
cd ~/baas-dcms
export VAULT_ADDR=http://127.0.0.1:8200
export PATH="$(pwd)/infra/vault/bin:$PATH"
export VAULT_TOKEN=<admin token>

infra/vault/apply.sh                                    # policies + AppRoles
infra/vault/apply.sh --seed                             # generate the machine-only secrets
infra/vault/provision-host.sh platform-api log-janitor  # issue this host its ids into .env

unset VAULT_TOKEN
```

`provision-host.sh` reads each role_id, mints a secret_id and writes both into `.env` — the
part that used to be four manual steps per service ending in a secret through a clipboard. It
is idempotent (a service with both ids set is left alone; `--rotate` replaces them), it
validates the token before touching anything, and it never prints or stores a secret value.
`--all` covers every service, which is what a brand-new host wants.

`PLATFORM_HOST` still belongs in `.env` by hand: it is not a secret, and Caddy needs it to
build a site address.

Until then the deploy rolls everything else and fails its health gate on platform-api, with the
instruction in that container's logs. That is deliberate: a green deploy that shipped a service
which cannot reach its database is worse than a red one that says which command is missing.

`PLATFORM_HOST` has a second reader, and forgetting that cost a sign-in outage.
`identity-migrate` -- not `identity` -- is the job that runs `IdentitySeeder`, and the seeder
registers the console's OIDC redirect URI as `https://$PLATFORM_HOST/auth/callback`. Config set
only on the `identity` service reaches the seeder never, so the client was registered against
`localhost` and OpenIddict refused every sign-in with *"The specified 'redirect_uri' is not
valid for this client application"*. Both services now merge one `x-vps-identity-seed` anchor,
and the seeder **converges** the two SPA clients' redirect URIs onto configuration instead of
only creating them -- so changing `PLATFORM_HOST` or `PUBLIC_BASE_URL` and redeploying repairs
a mis-registered client, and `identity-migrate`'s log names the old and new URIs when it does.
The Grafana client is still create-once: changing `GRAFANA_DOMAIN` means deleting that row
before the next deploy re-seeds it.

### Least privilege, and what it means when something breaks

platform-api connects as `dcms_platform`: `USAGE` on `obs`, read/write on
`platform.role_permissions`, and **no grant on any other schema**. Two consequences worth
knowing before debugging it:

- **It cannot write audit rows.** Records ride JetStream (`AddDcmsAuditOverNats`) and admin-api's
  writer appends them. If audit rows from the console are missing, look at NATS, not Postgres.
- **It cannot delete audit data**, and that is not a setting. There is no grant to remove.

### The console is empty or refuses everyone

- **A blank console for a real operator** — they hold no platform permission. `SuperAdmin`
  bypasses the check entirely; anyone else needs rows in `platform.role_permissions`. The
  Access page edits them, and a change takes effect within the 5-minute `pperm:` cache.
- **Storage page shows no stores** — it distinguishes the two causes. "No store sizes reported
  yet" means Prometheus answered and the `store-usage` sidecar has not; "not answering" means
  Prometheus is down.
- **Monitoring shows a blank frame** — Grafana refused to be framed. Check
  `GF_SECURITY_ALLOW_EMBEDDING` and that `frame-ancestors` names this host. Note the cookie
  works only because the console and Grafana share the registrable domain; moving the console
  to a different domain breaks it silently.

### Purging logs

Loki is the only real delete primitive. Selectors are narrowed server-side with
`category!="audit", category!="security"` and the effective selector is returned — audit and
security streams are held 90 days as an integrity control and aiming at them is refused.
Deletes apply after Loki's 2-hour delay and can be cancelled until then, from the same page.

Prometheus needs `--web.enable-admin-api` (set, with the exposure noted in `docker-compose.yml`);
Tempo has no delete API at all. Container-log truncation needs the `logjanitor` compose profile,
which is off by default because that sidecar holds write access to Docker's container directory.

### Somebody reports a trace id

This is the workflow the whole effort exists for. Every response carries
`X-Dcms-Trace-Id`; every failed request in the admin SPA shows it behind a copy
button; every browser-facing 5xx renders it on the error page; every RFC 9457
body carries it as `traceId`.

1. Open **Ops → Trace lookup** and paste the id.
2. Three panels resolve from that one string: the **Tempo trace** (the span tree,
   across every service and every NATS hop), the **Loki lines** tagged with it,
   and the matching **`audit.audit_events` rows**. The audit table has stored
   `TraceId` since ADR 0007, which is why the join works at all.
3. If the trace panel is empty but the others are not, the id is **older than 7
   days** — Tempo's retention. The audit row (400 days) and the log lines (30
   days; 90 for audit and security streams) are still there. That is a
   limitation, not a fault; say so to the reporter rather than hunting for a bug.

An `X-Dcms-Request-Id` works too, though it resolves to the audit rows and logs
rather than to a span tree.

### Retention at a glance

| Store | Kept | Cap |
|---|---|---|
| Prometheus (metrics) | 30 d | 8 GB |
| Loki (logs) | 30 d — **90 d** for audit/security streams | ~8 GB |
| Tempo (traces) | **7 d** | ~4 GB |
| `audit.audit_events` | 400 d, by partition | — |
| Docker json logs | 3 × 50 MB per container | ~3 GB |

Check the budget with `du -sh ~/dcms-data/{prometheus,loki,tempo,grafana}` on
vps1. The **Ops → Retention and disk** dashboard tracks the same numbers with a
fill-rate projection, and the host disk alert fires below 20 % free.

### The audit metrics now actually export

The six `Dcms.Audit` metrics in the table above were documented for a release
while exporting nothing: `AddMeter` was never called, and the OTel SDK silently
drops instruments from an unsubscribed meter. Because the headline guidance is
that `dcms.audit.recorded` **flat-lining** is the alarm, an unsubscribed meter and
a compromised platform produced identical graphs.

They are now subscribed via `DcmsMeters.All`, laid out on **Audit & security →
Audit health** against exactly the alert table above, and guarded by
`DcmsMeterRegistrationTests`, which fails the build if a meter defined in the
solution is not subscribed. If you add a meter, add it to `DcmsMeters`.

### Alerts

Rules live in `infra/observability/prometheus/rules/alerts.yml` (23 rules across
audit, availability, resources, infrastructure and security). They route through
a Grafana webhook to `POST /api/internal/alerts` on admin-api, which enqueues
onto the **`EMAIL` work queue** that email-worker already owns — so relay
credentials stay in exactly one container.

The webhook authenticates with a bearer token (`ALERT_WEBHOOK_SECRET`) compared
in constant time. Grafana's webhook contact point cannot sign a body, so this is
weaker than an HMAC and is treated accordingly: admin-api **refuses to start in
Production** on a secret shorter than 16 characters or drawn from a weak set, the
endpoint fails closed when no secret is set, and every accepted call is audited
as `observability.alert.notified`.

### Deployment gotchas that have already bitten

- **A config change had no effect.** Alloy's and Caddy's configs are bind-mounted *files*;
  a plain restart reads the old inode. `up -d --force-recreate <service>`.
- **A store restart-loops on `permission denied`.** Its data directory is owned by a
  different uid than the process. These are named volumes with `o: bind`, which Docker
  populates from the image — ownership included — so the image's own uid is the right one.
  Do not pin `user:` on them. Full explanation in `infra/observability/README.md`.

### Is the telemetry budget being kept?

```sh
./scripts/obs-disk-check.sh        # on vps1, from ~/baas-dcms
```

Prints each store's peak size, growth trend, projected size at the end of its retention
window, and its budget. Exit status is the number of stores projected over. The same series
drive three alerts, so this is the readable view rather than the control — there is nothing to
remember to run.

Projections are marked provisional until a day of history exists: a store's initial fill from
empty to steady state, extrapolated across thirty days, is a scare rather than a projection.

### When the dashboards themselves are the problem

- **A whole service missing from every panel** — `OTEL_EXPORTER_OTLP_ENDPOINT` is
  unset on it. The wiring is gated on that variable and silent without it.
- **Everything green but users cannot reach the site** — look at the `public-*` blackbox
  probes, not the per-service ones. The internal probes only prove a service answers on the
  compose network, which stays true when DNS, Caddy or a certificate is the problem.
- **Everything missing at once** — check `alloy`. Its UI (port 12345, not
  published; tunnel to it) shows every pipeline component's state and is the
  fastest way to see what stopped.
- **Grafana up, Postgres panels erroring** — the `dcms_grafana` role or the
  `obs.*` views are missing. The role is manual on an existing cluster
  (`infra/observability/README.md`, step 5); the views are applied by admin-api
  on start.
- **A Grafana login refused** — `role_attribute_strict` is working. The user is
  not a SuperAdmin, and a Grafana Viewer here could read every tenant's usage and
  every audit action on the platform.

### What is deliberately not covered

- **Direct-SQL writes.** Every service connects to Postgres as a cluster
  superuser, so a change made outside the application produces no audit row and
  no span. CDC is the control that would see it; it is designed and deferred
  (ADR 0008), with a reserved metric name and an empty panel on the security
  dashboard.
- **Automated Postgres backups.** vps1 has none. The retention dashboard and disk
  alert make the absence visible; they do not fix it.
