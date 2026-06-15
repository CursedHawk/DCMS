# Runbook

## Local development

Prereqs: .NET 10 SDK, Node 24 + pnpm, Docker Desktop.

```sh
docker compose up -d --build   # infra + all services
./scripts/smoke.ps1            # asserts health + provisioning
pnpm dev:admin                 # admin SPA with hot reload on :5173
```

Dev ports: admin SPA 5000 (container) / 5173 (vite), identity 5001,
admin-api 5002, content-api 5003, media-worker 5004, site-builder 5005,
site-host 5006, ai-gateway 5007, Postgres 5432, Redis 6379, NATS 4222
(monitor 8222), MinIO 9000 (console 9001), Vault 8200, Mailpit 8025.

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
SuperAdmins bypass tenant permission checks. Domain verification reads a TXT
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
`POST /api/admin/domains/{id}/site`. site-builder consumes the publish job and
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
map and runs a sandboxed offline `pnpm install` + `vite build` before uploading
`dist/`. The OpenAI-compatible provider is covered by an in-process stub test;
Transit and live model calls run against real infra/keys.

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

### Load smoke (Phase 12)

`k6 run scripts/load-smoke.js` ramps to 50 VUs against content-api's cached reads
(`/api/openapi.json`, `/api/{slug}/{type}`). Thresholds: <1% errors, p95 < 150 ms
over loopback (the internal cached-read target is p95 < 30 ms). Override with
`-e BASE=… -e TENANT=… -e SLUG=… -e CONTENT_TYPE=…`.

## Production profile

Run **without** the dev override:

```sh
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d
```

Differences from dev (`docker-compose.prod.yml`):

- **Caddy** is the only public ingress, terminating TLS for tenant custom domains
  via on-demand TLS. Caddy asks `site-host` `GET /internal/tls-allowed?domain=…`
  before minting a cert, so certificates are issued only for verified+linked
  domains. ACME email via `ACME_EMAIL`.
- **Vault** runs in real server mode (`infra/vault/config.hcl`, file storage, no
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
