# Repository Map

> Architectural map of `baas-dcms` at commit `22816b6` (2026-09-16), `master`.
> Built as the foundation for a later security / correctness / dead-code / architecture / testing / dependency audit.
> Nothing here is a verdict. Section 14 lists **audit targets**, not vulnerabilities.
> `UNKNOWN` marks anything the code did not answer.

---

## 1. Executive Overview

**What it is.** DCMS is a multi-tenant "Backend-as-a-Service / Dynamic CMS". A tenant (workspace) enables **plugins** (blog, galleries, forms, events, search, live chat, Instagram/Facebook feeds, branding, …). It writes content through a draft → version → publish flow and hosts websites on custom domains. There are three authoring modes:

| Mode | Source of truth | Editor | Build |
| --- | --- | --- | --- |
| **A**: static prerender | HTML/CSS files in a Forgejo git repo | GrapesJS visual builder (`apps/admin/src/features/builder`, `packages/gjs-*`) | `StaticSiteAssembler` in site-builder (~1 s) |
| **B**: React app | React/Vite project in a Forgejo git repo | Browser IDE (Monaco + esbuild-wasm preview) plus an in-browser AI agent | `ReactAppBuilder`: pnpm/npm/yarn install + vite build inside a throwaway sandbox container (~30 s) |
| **C**: static files | Uploaded zip or folder bundle | Upload page (`StaticSitePage.tsx`) | Unzip into object storage (~340 ms) |

Every tenant also gets a per-tenant, AI-readable **OpenAPI document** of its content API, plus a generated TypeScript client.

**Main applications**

- **10 .NET 10 services** (`src/Services`):
  - `identity`: OIDC server
  - `admin-api`: tenant admin plane, and owner of almost all DDL
  - `content-api`: public delivery plane
  - `platform-api`: cross-tenant operator console API
  - `ai-gateway`: LLM provider proxy
  - `edge`: YARP ingress with ACME TLS
  - `site-host`: custom-domain static host plus API proxy
  - `site-builder`: publish/build worker
  - `media-worker`: image, video and audio processing
  - `email-worker`: the only SMTP speaker
- **2 React 19 SPAs**:
  - `apps/admin`: the tenant admin, including builder, IDE and assistant
  - `apps/platform`: the platform operator console
- **8 real frontend packages** plus 1 manifest-only package (`site-builder-toolchain`).

**Backend architecture.** A service-per-bounded-context system. It shares **one PostgreSQL 18 database with a schema per context** (`tenancy`, `cms`, `media`, `sites`, `ai`, …). Services talk over **HTTP** (OAuth2 client-credentials between services) and **NATS JetStream** (events and work queues).

Inside each service the style is **ASP.NET Core Minimal APIs**. Endpoint lambdas inject EF Core DbContexts directly. There is **no controller layer, no application/domain/repository layering, no MediatR/AutoMapper/FluentValidation, and no API versioning**. Shared cross-cutting code lives in `src/Shared/*` libraries.

**Frontend architecture.**
- Vite + TypeScript, TanStack Router (code-based route tree), TanStack Query, Zustand, Radix UI + Tailwind v4 (via `@dcms/ui`), i18next (en/cs).
- OIDC authorization code + PKCE via `oidc-client-ts` (`@dcms/core`), with tokens stored in `localStorage`.
- `@microsoft/signalr` for live updates.

**Major infrastructure.**
- Data and messaging: PostgreSQL 18, Redis 7 (cache and SignalR backplane), NATS 2.11 JetStream, MinIO (S3 object store).
- Secrets and source: HashiCorp Vault (KV v2 app secrets and Transit encryption, AppRole per service, Transit auto-unseal against a separate seal Vault), Forgejo 11 (git for site source).
- Observability: Grafana LGTM stack (Alloy, Prometheus, Loki, Tempo, Grafana).
- Mail: Mailpit in dev.
- Everything runs on Docker Compose, deployed by GitLab CI over SSH to single hosts.

**Major external dependencies.**
- TLS: Let's Encrypt ACME (Certes) and the Cloudflare DNS API (DNS-01 wildcard).
- Sign-in and social: Google OAuth (SSO) and Meta Graph API (Instagram/Facebook OAuth and feeds).
- AI providers: Anthropic, OpenAI-compatible endpoints, Ollama, LM Studio.
- Mail relay: an SMTP relay (iCloud per docs).
- Media: FFmpeg (FFMpegCore) and SixLabors.ImageSharp 3.1.

---

## 2. Repository Structure

```
baas-dcms/
├── Dcms.sln                         # single solution, 48 projects (see §4)
├── Directory.Build.props            # net10.0, LangVersion=latest, Nullable, TreatWarningsAsErrors, InvariantGlobalization
├── Directory.Packages.props         # central NuGet versions (+ security pins: SSH.NET, System.Security.Cryptography.Xml)
├── global.json                      # SDK 10.0.100 rollForward latestFeature
├── package.json / pnpm-workspace.yaml / pnpm-lock.yaml   # pnpm@11.6.0 workspace (apps/*, packages/* except site-builder-toolchain)
├── package-lock.json                # stray npm lockfile at root (2.9 KB) — see §14
├── eslint.config.js / .prettierrc.json / playwright.config.ts
├── docker-compose.yml               # base stack: 38 services (infra + app + observability)
├── docker-compose.override.yml      # local dev (auto-loaded by bare `docker compose`)
├── docker-compose.prod.yml          # production overlay (sandboxed builds, socket proxy, pg_stat_statements…)
├── docker-compose.vps.yml           # vps host overlay (ports 80/443, nats-surveyor, AppRoles…)
├── .gitlab-ci.yml                   # build → test → images → promote → deploy
├── .env.example                     # ~80 env vars (secrets, hosts, AppRole ids)
├── CLAUDE.md / README.md
├── AI AGENT PROGRESS.md             # 988-line working notes (tracked)
├── TODO/                            # PROGRESS.md, AI AGENT IDE REWORK.md, GRAFANA AND TELEMETRY.md, FIXES…
├── docs/                            # setup, runbook, vault-secrets, seal-vault, deploy-linux, plugins, mode-a-builder, ai-agent
│   └── adr/                         # ADR 0001–0013
├── src/
│   ├── Services/                    # 10 deployable ASP.NET Core apps (all Sdk.Web, each with Dockerfile)
│   │   ├── Dcms.AdminApi/           # 100 files / 18.7k LOC — Ai, Analytics, ApiClientGen, Audit, Chat, Cms, Forms, Media,
│   │   │                            #   Notifications, Observability, Openapi, Plugins, Sites(+Git), Social, Tenancy
│   │   ├── Dcms.ContentApi/         # 24 files — Delivery, Chat(hub+bot), Forms, Visitors, Branding, Social, Plugins
│   │   ├── Dcms.Identity/           # 25 files — OpenIddict server, ASP.NET Identity, account pages, Forgejo user sync
│   │   ├── Dcms.PlatformApi/        # 20 files — Authz, Delegation(→admin-api), Observability, Purge, Realtime, Reporting, Stores
│   │   ├── Dcms.Edge/               # 39 files — Auth(OIDC cookie), Certificates(ACME, DNS-01), Protection, Routing, Transforms
│   │   ├── Dcms.SiteHost/           # 6 files — Host→tenant resolution, static serving, /api+/hub proxy
│   │   ├── Dcms.SiteBuilder/        # 7 files + sandbox.Dockerfile + Runtime/hydrate.js
│   │   ├── Dcms.AiGateway/          # 10 files — /v1/chat, /v1/messages, Providers/*
│   │   ├── Dcms.MediaWorker/        # 7 files — image/video/audio consumers
│   │   └── Dcms.EmailWorker/        # 2 files — EMAIL queue → SMTP
│   ├── Shared/                      # 12 class libraries (see §4.2)
│   │   └── Dcms.Shared.Data/        # 179 files / 22k LOC incl. EF migrations for 16 contexts
│   ├── PluginSdk/                   # Abstractions (IPlugin, manifest) + Runtime (registry, route table, OpenAPI assembler)
│   └── Plugins/                     # 19 plugin libraries + Dcms.Plugins.All aggregator (mostly 1 file each, declarative)
├── tests/
│   ├── Dcms.UnitTests/              # xUnit v3, NSubstitute, AwesomeAssertions — ~230 tests
│   ├── Dcms.PluginSdk.Tests/        # 59 tests
│   └── Dcms.IntegrationTests/       # Testcontainers (Postgres/Redis/NATS/MinIO) + WebApplicationFactory — ~290 tests
├── apps/
│   ├── admin/                       # 268 TS/TSX files / 45k LOC — tenant admin SPA (nginx image)
│   └── platform/                    # 46 files / 4k LOC — operator console SPA (nginx image)
├── packages/
│   ├── core/                        # oidc auth factory, fetch client, runtime-config, permission helpers
│   ├── ui/                          # design system (Radix wrappers, shell, DataTable, RequirePermission, live, tour)
│   ├── api-client/                  # tenant site HTTP runtime (also EMBEDDED into admin-api for client generation)
│   ├── site-components/             # chat widget (SignalR) for tenant sites
│   ├── gjs-schema/                  # zod schemas: site/component/theme/placeholder specs
│   ├── gjs-parse/                   # htmlparser2 + css-tree parse/format/lint/diff for Mode A
│   ├── gjs-blocks/                  # GrapesJS blocks, kits, traits, thumbnails, render parity
│   ├── site-template-react/         # Mode B starter templates (blank/content/landing) — EMBEDDED into admin-api
│   └── site-builder-toolchain/      # package.json only: dependency palette for IDE type hints (excluded from workspace)
├── e2e/                             # Playwright: admin/, platform/, mobile/, fixtures/ (mocked server)
├── infra/
│   ├── postgres/                    # bootstrap.sh + init/00-schemas.sql, 01-rls.sql, 02..05 role scripts
│   ├── nats/                        # nats.conf, provision-streams.sh
│   ├── minio/init.sh
│   ├── vault/                       # apply.sh, init.sh, provision-host.sh, services.sh, policies/*.hcl, seal/, server/, bin/vault
│   ├── observability/               # alloy, prometheus(+rules), loki, tempo, grafana(dashboards, provisioning)
│   ├── log-janitor/                 # Python container log truncation service (Dockerfile + janitor.py)
│   └── acme-test/                   # Pebble config for local ACME testing
├── scripts/
│   ├── deploy.sh                    # 699-line host deploy orchestration (run on the host by CI)
│   ├── ci/deploy-remote.sh, ci/resolve-digests.sh
│   ├── obs-*.sh, smoke.ps1
│   └── ai-bench/ai-bench.mjs        # AI agent benchmark (benchmarks/ai-agent.json)
├── loadtest/                        # k6 scenarios (admin, delivery, media, publish, ratelimit, sitebuild, sitehost, mixed)
└── (untracked/ignored build output) graphify-out/, node_modules/, apps/*/dist, playwright-report/, test-results/,
                                     src/Shared/Dcms.Shared.Data/bin\Debug/ (a directory literally named with a backslash, gitignored)
```

---

## 3. Technology Stack

| Area | Technology | Version | Evidence |
| --- | --- | --- | --- |
| Backend runtime | .NET / ASP.NET Core (Minimal APIs) | net10.0, SDK 10.0.100 | `Directory.Build.props`, `global.json` |
| C# | LangVersion `latest` (C# 14 with SDK 10), nullable, warnings-as-errors | — | `Directory.Build.props` |
| Frontend | React | ^19 (toolchain pins 19.2.7) | `apps/*/package.json` |
| Frontend build | Vite + TypeScript (`tsc -b && vite build`), Tailwind v4 (`@tailwindcss/vite`) | vite/ts versions in lockfile | `apps/admin/package.json` |
| Routing / data | TanStack Router ^1, TanStack Query ^5, Zustand ^5 | — | same |
| UI | Radix UI, lucide-react, sonner, framer-motion, cmdk, recharts | — | same |
| Editors | GrapesJS ^0.23.5, Monaco ^0.52, esbuild-wasm ^0.25, TipTap ^3, RJSF ^5 (JSON-Schema forms), @scalar/api-reference-react | — | `apps/admin/package.json` |
| Validation (FE) | zod ^4 | — | `apps/admin`, `packages/gjs-schema` |
| Package manager | pnpm | 11.6.0 | root `package.json` `packageManager` |
| Node (CI) | node:24-alpine | 24 | `.gitlab-ci.yml` |
| Database | PostgreSQL (pg_trgm, pg_stat_statements) | 18 | `docker-compose.yml`, `infra/postgres/init/00-schemas.sql` |
| ORM | EF Core + Npgsql provider | Npgsql.EFCore.PostgreSQL 10.0.2 | `Directory.Packages.props` |
| Multi-tenancy | Finbuckle.MultiTenant (header strategy `X-Dcms-Tenant`) | 10.1.1 | `TenancyServiceCollectionExtensions.cs` |
| Cache / backplane | Redis (StackExchange.Redis), SignalR Redis backplane | redis:7, SE.Redis 2.13.17 | compose, `Dcms.Shared.Caching` |
| Messaging | NATS JetStream (NATS.Net) | nats:2.11, NATS.Net 2.8.1 | compose, `Dcms.Shared.Messaging` |
| Object storage | MinIO (Minio SDK) | quay.io/minio/minio:latest, SDK 7.0.0 | compose, `Dcms.Shared.Storage` |
| Secrets | HashiCorp Vault KV v2 + Transit (VaultSharp) | hashicorp/vault:latest, VaultSharp 1.17.5.1 | `Dcms.Shared.Vault`, `infra/vault` |
| Authentication (server) | OpenIddict server + ASP.NET Core Identity + Google OAuth | OpenIddict 7.5.0, AspNetCore 10.0.9 | `Dcms.Identity/Program.cs` |
| Authentication (resource) | JwtBearer against identity discovery | 10.0.9 | `Dcms.Shared.Security/AuthServiceCollectionExtensions.cs` |
| Authentication (SPA) | oidc-client-ts (code + PKCE, localStorage store) | ^3 | `packages/core/src/auth.ts` |
| Authentication (edge) | Cookie + OpenIdConnect (for Grafana/Forgejo hosts) | 10.0.9 | `Dcms.Edge/Auth` |
| Authorization | Custom dynamic policy provider (`dcms.perm:` / `dcms.pperm:`), DB-backed resolvers | — | `Dcms.Shared.Security/Authorization` |
| Ingress | YARP reverse proxy (custom .NET edge), Certes ACME | Yarp 2.3.0, Certes 3.0.4 | `Dcms.Edge` |
| Realtime | ASP.NET Core SignalR (4 hubs) | — | `MapHub` calls (§5) |
| AI SDKs | Anthropic .NET, OpenAI .NET | 12.29.0 / 2.11.0 | `Dcms.AiGateway.csproj` |
| Media | SixLabors.ImageSharp (pinned 3.x), FFMpegCore | 3.1.12 / 5.4.0 | ADR 0001 |
| Email | MailKit | 4.17.0 | `Dcms.Shared.Messaging/Email` |
| OpenAPI | Microsoft.OpenApi, JsonSchema.Net, YamlDotNet, Scalar | 3.7.0 / 9.2.1 / 16.3.0 / 2.16.3 | `Directory.Packages.props` |
| Observability | OpenTelemetry (OTLP gRPC → Alloy), Serilog | OTel 1.16.0, Serilog.AspNetCore 10.0.0 | `Dcms.Shared.Hosting` |
| Obs stack | Alloy, Prometheus, Loki, Tempo, Grafana | v1.19.1 / v3.7.3 / 3.6.0 / 2.10.0 / 12.3.1 | `docker-compose.yml` |
| Testing (.NET) | xUnit v3, AwesomeAssertions, NSubstitute, Testcontainers, Respawn, Mvc.Testing, JunitXml | 3.2.2 / 9.4.0 / 5.3.0 / 4.12.0 / 7.0.0 | `Directory.Packages.props` |
| Testing (FE) | Vitest, Testing Library, jsdom | — | `apps/*/package.json` |
| E2E | Playwright (+ @axe-core/playwright) | ^1.62.1 | root `package.json`, `playwright.config.ts` |
| Load testing | k6 | UNKNOWN version | `loadtest/lib/k6.js` |
| Lint / format | ESLint 9 flat config (typescript-eslint, react-hooks, jsx-a11y), Prettier 3; .NET analyzers | — | `eslint.config.js` |
| Build (images) | Docker multi-stage per service, BuildKit, GitLab registry | docker:27 | `*/Dockerfile`, `.gitlab-ci.yml` |
| Deployment | Docker Compose (base + prod/vps overlays), image digests, SSH from CI | — | `scripts/deploy.sh`, `scripts/ci/*` |
| Git server | Forgejo | 11 | compose `forgejo` |

---

## 4. Backend Architecture

### 4.1 Project inventory and reference graph

| Project | Kind | References (project) | Notable packages |
| --- | --- | --- | --- |
| **Dcms.AdminApi** | Web API + many hosted workers | PluginSdk.Abstractions, Plugins.All, Shared.{Caching, Data, Media, Hosting, Messaging, Security, Storage, Vault} | DnsClient, JsonSchema.Net, SignalR.Redis |
| **Dcms.ContentApi** | Public Web API + SignalR | PluginSdk.Runtime, Plugins.All, Shared.{Caching, Data, Hosting, Messaging, Security, Storage} | SignalR.Redis, YamlDotNet |
| **Dcms.Identity** | OIDC server + server-rendered account pages | Shared.{Data, Hosting, Messaging} | OpenIddict.AspNetCore/EFCore, Identity.EFCore, Auth.Google |
| **Dcms.PlatformApi** | Operator console API + SignalR | Shared.{Caching, Data, Hosting, Messaging, Security, Vault} | SignalR.Redis |
| **Dcms.AiGateway** | Internal API (LLM proxy) | Shared.{Caching, Data, Hosting, Security, Vault} | Anthropic, OpenAI |
| **Dcms.Edge** | Reverse proxy / TLS terminator | Shared.{Caching, Contracts, Data, Hosting, Messaging, Vault} | Yarp, Certes, DnsClient, OIDC |
| **Dcms.SiteHost** | Static host + proxy | Shared.{Caching, Data, Hosting, Messaging, Storage} | Yarp (HttpForwarder) |
| **Dcms.SiteBuilder** | Worker (web host for health) | Shared.{Data, Hosting, Messaging, Storage} | — (shells out to `docker`/package managers) |
| **Dcms.MediaWorker** | Worker | Shared.{Data, Media, Hosting, Messaging, Storage} | FFMpegCore |
| **Dcms.EmailWorker** | Worker | Shared.{Hosting, Messaging} | — |
| Dcms.Shared.Kernel | Lib (no deps) | — | `ITenantContext`, `ICurrentActor`, `ISandboxContext`, `IClock`, `Guard`, `Result` |
| Dcms.Shared.Contracts | Lib (no deps) | — | Event records (`Events/*`), `Streams`/`Subjects` |
| Dcms.Shared.Audit | Lib | Contracts, Kernel | `IAuditRecorder`, `AuditMiddleware`, redaction, propagation |
| Dcms.Shared.Telemetry | Lib | Audit | ActivitySource, meters, sensitive-attribute processor |
| Dcms.Shared.Vault | Lib | — | VaultSharp config provider, token refresh, Transit encryptor |
| Dcms.Shared.Security | Lib | Audit, Kernel | JwtBearer setup, permission constants/policies, `ServiceTokenClient` |
| Dcms.Shared.Caching | Lib | — | `ICacheService` over Redis |
| Dcms.Shared.Storage | Lib | Kernel | `IObjectStorage` over MinIO |
| Dcms.Shared.Media | Lib | Contracts | `ContentSniffer`, `MediaSanitizer`, `SvgSanitizer`, `WebpLadderGenerator` |
| Dcms.Shared.Messaging | Lib | Audit, Contracts, Kernel, Telemetry | NATS publisher/health, `NatsAuditSink`, email queue + `SmtpEmailSender` |
| Dcms.Shared.Hosting | Lib | Audit, Telemetry, Vault | `AddDcmsServiceDefaults`, ProblemDetails, rate limiting, security headers, health endpoints |
| Dcms.Shared.Data | Lib | Audit, Contracts, Kernel, Vault | All shared DbContexts, migrations, RLS, audit chain, Data Protection, Finbuckle store |
| Dcms.PluginSdk.Abstractions | Lib | Kernel | `IPlugin`, `PluginManifest`, `ContentTypeDefinition`, `IPluginEndpointBuilder` |
| Dcms.PluginSdk.Runtime | Lib | Abstractions, Caching, Contracts, Messaging, Security | `PluginRegistry`, `PluginRouteTable`, `OpenApiAssembler`, DI helpers |
| Dcms.Plugins.* (19) | Libs | PluginSdk.Abstractions (+ Meta.Core for Facebook/Instagram) | — |
| Dcms.Plugins.All | Aggregator | Runtime + all 19 plugins | `DcmsPluginSet.AddAll()` |

**Dependency direction.** `Kernel`/`Contracts` sit at the leaves. `Audit`/`Telemetry`/`Vault` depend on them. `Security`, `Messaging`, `Hosting` and `Data` build on those. Services reference whichever shared libs they need.

No shared lib references a service. **Exception worth noting:** `Dcms.UnitTests` references `Dcms.PlatformApi`, `Dcms.Edge` and `Dcms.AiGateway` directly, and `Dcms.IntegrationTests` references every service. That is normal for tests.

Plugins depend only on `PluginSdk.Abstractions`. They are pure metadata and declarations, with no DB or HTTP code of their own.

### 4.2 Architectural pattern

- **Pattern:** "vertical-slice Minimal API per bounded context over a shared database".
  - Each feature folder (for example `AdminApi/Cms`) contains a static `Map*Endpoints(this IEndpointRouteBuilder)` extension whose lambdas inject `DbContext`, `CurrentUser`, `ITenantContext`, `IEventPublisher`, `IAuditRecorder` and so on.
  - Business rules, persistence and DTO shaping live inline in those lambdas or in a few helper services (`TenantProvisioning`, `SiteGitService`, `MediaIngestService`, `MetaFeedSyncService`, `PublishedContentReader`, `AiProviderResolver`).
- **No repository layer.** EF DbContexts are used directly. Where LINQ is insufficient, hand-written SQL lives in `Shared.Data` (`ContentListQueries`, `TagQueries`, `ObservabilityQuery`).
- **DTOs** are anonymous objects or local records next to endpoints. Frontend TypeScript types are hand-written (no shared codegen between backend and SPAs). The exception is `Dcms.AdminApi/ApiClientGen/TypeScriptClientEmitter.cs`, which generates a **tenant site** client from the tenant OpenAPI document.
- **Validation** is ad hoc inside handlers. Plugin instance config is validated against the plugin's JSON Schema (`PluginConfigValidator`, JsonSchema.Net). `AiBaseUrl` validates provider base URLs.
- **Schema ownership:** admin-api owns and migrates every shared schema (ADR 0003), and identity owns its own. Other services register the DbContexts they read. Least-privilege DB roles exist for site-builder (`dcms_sitebuilder`), edge (`dcms_edge`), platform-api (`dcms_platform`), Grafana (`dcms_grafana`), raw RLS access (`dcms_rls`) and the NOBYPASSRLS runtime role of ADR 0015 (`dcms_app`, created but not yet connected to).
- **Async integration:** a transactional outbox (`cms.content_outbox`, `audit.audit_outbox`) is drained by hosted services, which publish to JetStream. Consumers are `BackgroundService`s using durable consumers.

### 4.3 Entry points (all `Program.cs`, top-level statements)

| Service | File | Pipeline highlights (in order) | Special modes / guards |
| --- | --- | --- | --- |
| admin-api | `src/Services/Dcms.AdminApi/Program.cs` | ProblemDetails → ForwardedHeaders (all proxies trusted) → Audit → AuthN → **Finbuckle MultiTenant** → **TenantMembership** → **ServicePrincipalGuard** → **PropagatedActor** → AuthZ → RateLimiter (named policy only) | `--migrate-only` runs `DcmsMigrationRunner` (all EF migrations, audit schema, RLS, obs views) and exits. Production refuses a weak `Forgejo:WebhookSecret` or `Alerting:WebhookSecret`. |
| content-api | `src/Services/Dcms.ContentApi/Program.cs` | ProblemDetails → ForwardedHeaders → SecurityHeaders → RateLimiter (global `AddDcmsRateLimiting`) → CORS → Audit → MultiTenant → AuthN → AuthZ | Production refuses a missing/default/short `Visitor:SigningKey` |
| identity | `src/Services/Dcms.Identity/Program.cs` | ProblemDetails → ForwardedHeaders → Audit → CORS → AuthN → AuthZ | `--migrate-only` (migrate and seed). Outside Development, refuses to start without signing/encryption certificates unless `Identity:AllowEphemeralKeys`. |
| platform-api | `src/Services/Dcms.PlatformApi/Program.cs` | ProblemDetails → Audit → AuthN → AuthZ | Refuses to start without `ConnectionStrings:Postgres` (Vault-only). Calls `ValidateDcmsResourceAuthentication()`. |
| ai-gateway | `src/Services/Dcms.AiGateway/Program.cs` | ProblemDetails → Audit → AuthN → AuthZ | — |
| edge | `src/Services/Dcms.Edge/Program.cs` | ProblemDetails → **UntrustedHeaderScrubbing** → request metrics → HTTPS redirect / HSTS → defaults → AuthN → AuthZ → RateLimiter → OutputCache (optional) → `/.edge/*` → ACME challenge → ReverseProxy (+ public-plane header scrubbing) | TLS via `EdgeTlsConfigurator` (SNI) when `Edge:Certificates:TlsEnabled`. ChallTestSrv DNS requires the insecure-ACME switch too. |
| site-host | `src/Services/Dcms.SiteHost/Program.cs` | ProblemDetails → ForwardedHeaders → SecurityHeaders → `/internal/tls-*` → `/api`, `/hub` proxy → `/{**path}` static | — |
| site-builder | `src/Services/Dcms.SiteBuilder/Program.cs` | ProblemDetails + defaults only | One `SitePublishConsumer` per `SiteBuildLane` |
| media-worker | `src/Services/Dcms.MediaWorker/Program.cs` | ProblemDetails + defaults | — |
| email-worker | `src/Services/Dcms.EmailWorker/Program.cs` | ProblemDetails + defaults | — |

Every service calls `builder.AddDcmsServiceDefaults(name[, AuditProfile])` (`Dcms.Shared.Hosting/DcmsHostingExtensions.cs:66`), which sets up:
- the Vault configuration provider
- Serilog
- OpenTelemetry (OTLP export only if `OTEL_EXPORTER_OTLP_ENDPOINT` is set)
- health checks
- ProblemDetails (`DcmsExceptionHandler : IExceptionHandler`)
- audit registration

`MapDcmsDefaultEndpoints` maps `/health` (all checks) and `/health/live` (no checks).

There is **no CLI tool project**. Operational CLIs are shell (`scripts/`, `infra/*`), Python (`infra/log-janitor/janitor.py`) and Node (`scripts/ai-bench`, `loadtest`).

### 4.4 Hosted services / background workers

**admin-api** (`Program.cs`):
- Migration: `TenancyMigrator`, which migrates at startup unless disabled (the deploy uses the `--migrate-only` job).
- Tenancy and content: `MembershipChangedConsumer`, `OutboxDispatcher` (cms outbox → NATS), `ScheduledPublishWorker` (DB polling).
- Analytics and chat: `AnalyticsConsumer`, `AnalyticsRetentionWorker`, `ChatFanoutConsumer`.
- Notification consumers: `SitePublishedNotificationConsumer`, `SiteBuildFailedNotificationConsumer`, `MediaProcessedNotificationConsumer`, `MediaFailedNotificationConsumer`, `ContentPublishedNotificationConsumer`, `ContentUnpublishedNotificationConsumer`, `DomainVerifiedNotificationConsumer`, `PluginInstanceNotificationConsumer`, `NotificationIngestConsumer`.
- Retention and expiry: `InvitationExpiryWorker`, `NotificationRetentionWorker`, `AiConversationRetentionWorker`, `CertificateNotificationWorker`.
- Audit: `AuditChainWriter`, `AuditIngestConsumer`, `AuditMaintenanceWorker`, `AuditLogProjector`.
- Meta social, gated by `Social:SyncEnabled`: `MetaSyncWorker`, `MetaTokenRefreshWorker`.

**content-api:** `ContentCacheInvalidator`, `SearchIndexer`.

**identity:** `IdentitySeeder` (migrate and seed: roles, SuperAdmin, scopes, OAuth clients), `ForgejoSyncWorker` (password/credential outbox).

**platform-api:** `PlatformRoleSeeder`, `PlatformLiveUpdates` (NATS → SignalR), `PlatformSampleBroadcaster`.

**edge:**
- Routing: `EdgeConfigInvalidator`.
- Certificates: `CertificateRenewalService`, `DomainCertificateProvisioner`, `ManagedCertificateInvalidator`, `EdgeTlsPreflight`.

**site-host:** `SiteCacheInvalidator`, `TenantStatusInvalidator`.

**site-builder:** `SitePublishConsumer` × lanes (`SiteBuildLane.All()`).

**media-worker:** `ImageProcessingConsumer`, `VideoProcessingConsumer`, `AudioProcessingConsumer` (base `MediaConsumerBase`).

**email-worker:** `EmailSendConsumer`.

### 4.5 Middleware, filters, cross-cutting

| Concern | Where |
| --- | --- |
| Exception → ProblemDetails with trace id | `Dcms.Shared.Hosting/DcmsProblemDetails.cs` (`UseDcmsProblemDetails`) |
| Audit capture per request (+ `.WithAudit(action, resource)`, `.AuditExempt(reason)` endpoint metadata) | `Dcms.Shared.Audit/Http/AuditMiddleware.cs`, `AuditEndpointExtensions.cs`; EF interceptors in `Shared.Data/Audit/*` (`AuditOutboxInterceptor`, `AuditBulkCommandInterceptor`) |
| Audit propagation across service hops (headers) | `Shared.Audit/Http/AuditPropagationHandler.cs`, `Propagation/AuditPropagation.cs`; restored in admin-api by `PropagatedActorMiddleware` |
| Tenant membership / suspension gate | `AdminApi/Tenancy/TenantMembershipMiddleware.cs` (`AllowNonMemberTenant` exemption attribute) |
| Service-token confinement | `AdminApi/Tenancy/ServicePrincipalGuard.cs` (`AllowServicePrincipal(scope)`, `AllowConsoleService` = `dcms.console`) |
| Rate limiting | content-api: `AddDcmsRateLimiting` (global, excludes `/health`); admin-api: named policy on Meta OAuth callback only; edge: `Protection/EdgeRateLimiting.cs` (global + `AuthPolicy` on auth host) |
| Security headers (X-Frame-Options DENY, Referrer-Policy, optional CSP) | `DcmsHostingExtensions.UseDcmsSecurityHeaders` — used by content-api and site-host only |
| Output cache (off by default) | `Dcms.Edge/Protection/EdgeOutputCache.cs` |
| Header scrubbing (X-Forwarded-*, X-WEBAUTH-*, X-Dcms-Tenant/Sandbox on public plane) | `Dcms.Edge/Transforms/HeaderScrubbing.cs` |
| Identity headers to Grafana/Forgejo | `Dcms.Edge/Transforms/IdentityHeaders.cs` |
| Forwarded headers | admin-api, content-api, identity, site-host, all with `KnownIPNetworks/KnownProxies.Clear()` |
| CORS | content-api (no default policy; any-origin anonymous policies for `collect`, form submit, branding read only); identity (default policy, origins from config, dev localhost fallback). No CORS on admin-api/platform-api (same origin behind edge). |
| Antiforgery | Not registered. `.DisableAntiforgery()` on identity form posts and multipart uploads. |
| API versioning | **None** |
| Controllers / MVC filters | **None** (Minimal APIs only) |

---

## 5. API Map

Edge host routing (`src/Services/Dcms.Edge/Routing/PlatformRoutes.cs`, plus DB overlay `edge.routes` via `DatabaseRouteSource`):

| Host | Path | Destination | Edge policy |
| --- | --- | --- | --- |
| `AUTH_HOST` (auth.highgeek.eu) | `/**` | identity | Auth rate-limit policy |
| `PLATFORM_HOST` | `/api/platform/**` | platform-api | — |
| `PLATFORM_HOST` | `/api/identity/**` | identity | — |
| `PLATFORM_HOST` | `/**` | platform-spa (nginx) | — |
| `ADMIN_HOST` | `/api/**` | admin-api | — |
| `ADMIN_HOST` | `/hub/**` | content-api | — |
| `ADMIN_HOST` | `/**` | admin-spa (nginx) | — |
| `GRAFANA_DOMAIN` | `/**` | grafana | `SuperAdmin` (when edge OIDC secret set), identity headers |
| `GIT_HOST` | git smart-HTTP paths, `/api/v1/**`, `/api/internal/**` | forgejo | **none** (git clients authenticate to Forgejo) |
| `GIT_HOST` | `/**` | forgejo | `SignedIn` (when enabled), identity headers |
| any other host | `/**` | site-host | optional output cache |
| any host | `/.well-known/acme-challenge/{token}`, `/.edge/signin`, `/.edge/signout…`, `/.edge/denied` | edge itself | — |

Auth legend: **P(x)** = `RequirePermission(x)` (tenant permission, SuperAdmin bypass); **PP(x)** = `RequirePlatformPermission(x)`; **Auth** = `RequireAuthorization()`; **Anon** = anonymous; **NM** = `AllowNonMemberTenant`; **CS** = `AllowConsoleService` (dcms.console service token accepted); **A** = `.WithAudit`.

### 5.1 admin-api (`/api/...` on ADMIN_HOST)

| Method & route | Auth | File |
| --- | --- | --- |
| GET `/api/admin/me` | Auth, NM | `Program.cs` |
| GET `/api/admin/ai/ping` | Auth (diagnostic; calls ai-gateway `/internal/ping`) | `Program.cs` |
| GET `/api/admin/me/tenants`, `/api/admin/me/permissions` | Auth, NM | `Tenancy/TenancyEndpoints.cs` |
| POST `/api/admin/tenants` (A) · GET `/api/admin/tenants` | Auth + in-handler `IsSuperAdmin` → `Forbid()` | same |
| POST `/api/admin/tenants/{id}/suspend` · `/resume` | Auth, CS | same |
| GET/POST `/api/admin/roles`, PUT/DELETE `/api/admin/roles/{id}` | P(roles:manage) | same |
| GET `/api/admin/members`, POST/DELETE `/api/admin/members/{mid}/roles[/{roleId}]` | P(members:manage) | same |
| GET `/api/admin/permissions/catalog` | Auth | same |
| GET/PATCH/DELETE `/api/admin/tenant`, POST `/api/admin/tenant/transfer` | P(tenant:settings) | `Tenancy/TenantAdminEndpoints.cs` |
| GET `/api/admin/me/account`, DELETE `/api/admin/me` | Auth, NM | `Tenancy/MyAccountEndpoints.cs` |
| GET/POST `/api/admin/invitations`, POST `…/{id}/resend`, DELETE `…/{id}` | P(members:manage) | `Tenancy/InvitationEndpoints.cs` |
| POST `/api/admin/invitations/accept` | Auth, NM | same |
| GET/POST `/api/admin/domains`, POST `/domains/provisioned`, POST `/domains/{id}/verify\|site\|primary`, DELETE `/domains/{id}` | P(domains:manage) | `Tenancy/DomainEndpoints.cs` |
| GET `/api/admin/domains/certificates`, POST `/domains/{id}/certificate/reissue`, PUT/DELETE `/domains/{id}/certificate` (custom cert upload) | P(domains:manage) | `Tenancy/DomainCertificateEndpoints.cs`, `CertificateUpload.cs` |
| GET/POST `/api/admin/platform/certificates`, GET `…/{id}/attempts`, PUT/DELETE `…/{id}`, POST `…/{id}/reissue` | Auth + CS (handler checks `ConsoleCaller.Allowed`) | `Tenancy/ManagedCertificateEndpoints.cs` |
| GET `/api/admin/plugins/catalog` | Auth | `Plugins/PluginInstanceEndpoints.cs` |
| GET/POST `/api/admin/plugins/instances`, PUT `…/{id}`, POST `…/{id}/{action}` | P(plugins:manage) | same |
| GET `/api/admin/navigation` | Auth, NM | `Plugins/NavigationEndpoints.cs` |
| GET `/api/admin/marketplace` | P(plugins:manage) | `Plugins/MarketplaceEndpoints.cs` |
| GET `/api/admin/content`, `/content/{id}`, `/content/tags`, `/content/page`, `/content/counts`, `/content/scheduled` | P(content:read) | `Cms/ContentEndpoints.cs`, `Cms/ContentListEndpoints.cs` |
| POST `/api/admin/content`, PUT `/content/{id}` | P(content:write) | `Cms/ContentEndpoints.cs` |
| POST `/content/{id}/publish\|unpublish\|schedule`, DELETE `/content/{id}/schedule` | P(content:publish) | same |
| GET `/api/admin/forms`, `/forms/{instanceId}/submissions` | P(content:read) | `Forms/FormSubmissionEndpoints.cs` |
| POST `/forms/submissions/{id}/handled`, DELETE `/forms/submissions/{id}` | P(content:write) | same |
| POST `/api/admin/media` (multipart) | P(media:write), DisableAntiforgery | `Media/MediaEndpoints.cs` → `MediaIngestService` |
| GET `/api/admin/media`, `/media/usage`, `/media/folders`, `/media/{id}`, `/media/{id}/content` | P(media:read) | same |
| POST/PATCH/DELETE `/media/folders[/{id}]`, PATCH `/media/{id}`, POST `/media/move`, POST `/media/delete` | P(media:write) | same |
| GET/POST `/api/admin/sites`, GET `/sites/{id}`, GET `/sites/{id}/builds`, GET `…/builds/{bid}/log` | P(site:edit) | `Sites/SiteEndpoints.cs` |
| POST `/sites/{id}/upload` (Mode C bundle, multipart) | P(site:edit), DisableAntiforgery | same, `StaticBundleBuilder.cs` |
| PUT `/sites/{id}/definition` | P(site:edit) | same (no SPA caller found — §7.3) |
| GET `/sites/{id}/ide`, PATCH `/sites/{id}/ide/files` | P(site:edit) | same |
| POST `/sites/{id}/publish`, POST `/sites/{id}/builds`, POST `…/builds/{bid}/activate` | P(site:publish) | same |
| POST `/sites/{id}/git/provision` · GET `/git`, `/git/branches`, `/git/history`, `/git/changes`, `/git/compare` · POST `/git/commit`, `/git/branches`, `/git/restore` | P(site:edit) | same → `Sites/Git/SiteGitService*.cs`, `ForgejoClient` |
| POST `/sites/{id}/git/merge`, `/git/merge/resolve` | P(site:publish) | same (`SiteGitService.LocalMerge.cs` shells out to `git`) |
| **POST `/api/internal/git/webhook`** | **Anon**, HMAC (`Forgejo:WebhookSecret`) | `Sites/SiteEndpoints.cs:964` |
| DELETE `/api/admin/sites/{id}` | P(site:publish) | `Sites/SiteDeletion.cs` |
| ANY `/api/admin/sites/{siteId}/preview/api/{**path}` (proxy to content-api with tenant + sandbox headers) | site:edit (see comment line 39) | `Sites/SitePreviewEndpoints.cs` |
| POST `/sites/{siteId}/preview/sandbox/reset` | P(site:edit) | same |
| GET `/api/admin/sites/{siteId}/starter-files`, `/generated-files`, GET `/api/admin/api-fingerprint` | P(site:edit) | `ApiClientGen/SiteScaffoldEndpoints.cs` |
| GET `/api/admin/api-client.zip`, `/api/admin/site-starter.zip` | Auth | `ApiClientGen/ApiClientEndpoints.cs` |
| GET `/api/admin/openapi.json` | Auth | `Openapi/OpenApiPreviewEndpoints.cs` |
| GET/PUT `/api/admin/ai/settings` | P(ai:settings) | `Ai/AiSettingsEndpoints.cs` |
| GET/PUT/DELETE `/api/admin/ai/user-credentials` | P(site:edit) | `Ai/AiAgentEndpoints.cs` |
| POST `/api/admin/ai/messages` (streaming proxy → ai-gateway `/v1/messages`) | P(site:edit) | same |
| POST `/api/admin/ai/generate/block\|page\|site` | P(site:edit) | `Ai/AiGenerationEndpoints.cs` |
| GET/POST `/api/admin/ai/conversations`, GET/PATCH/DELETE `…/{id}`, POST `…/{id}/messages`, PUT `…/{id}/runs/{runId}` | P(UsePermission — constant in file) + owner checks | `Ai/AiConversationEndpoints.cs` |
| GET `/api/admin/analytics`, `/analytics/dimensions` | P(analytics:read) | `Analytics/AnalyticsDashboardEndpoints.cs` |
| DELETE `/api/admin/analytics` | P(tenant:settings) | same |
| POST `/api/admin/analytics/prune` | Auth, NM, CS | `Analytics/AnalyticsPruneEndpoints.cs` |
| GET `/api/admin/audit`, `/audit/verify`, `/audit/{id}` · GET `/audit/export` | P(audit:read) · P(audit:export) | `Audit/AuditEndpoints.cs` |
| GET `/api/admin/chat/conversations`, `…/{cid}/messages` | P(chat:read) | `Chat/ChatConsoleEndpoints.cs` |
| GET `/api/admin/notifications`, `/unread-count`, `/{id}`; POST `/{id}/read`, `/read-all`, `/{id}/dismiss` | Auth | `Notifications/NotificationEndpoints.cs` |
| GET `/api/admin/platform/notifications[/unread-count]`, POST `…/{id}/read`, `/read-all`, `/{id}/dismiss` | Auth + CS | `Notifications/PlatformNotificationEndpoints.cs` |
| GET `/api/admin/social/{provider}/connect`, GET `/social/connections`, POST `/social/connections/{id}/disconnect`, POST `/social/instances/{iid}/sync` | P(plugins:manage) | `Social/MetaOAuthEndpoints.cs` |
| **GET `/api/admin/social/callback`** | **Anon**, rate-limited (20/min per IP), state token | same |
| POST (Route const) Instagram stories | Auth + `AllowServicePrincipal(dcms.social)` (called by content-api) | `Social/MetaStoriesEndpoints.cs` |
| **POST `/api/internal/alerts`** (Route const) | **Anon**, `Alerting:WebhookSecret` | `Observability/AlertEndpoints.cs` |
| Hub `/api/hub/notifications` | `[Authorize]`, token via `access_token` query (scoped to `/api/hub`) | `Notifications/NotificationHub.cs` |
| Hub `/api/hub/sites` | `[Authorize]` | `Sites/SiteHub.cs` |

### 5.2 content-api (reached via site-host on tenant domains, or `/hub` on ADMIN_HOST)

Tenant resolution: `X-Dcms-Tenant` header (slug). site-host overwrites it from the `Host` → domain lookup. `{slug}` in routes is the **plugin instance slug**, not the tenant.

| Method & route | Auth / CORS | File |
| --- | --- | --- |
| GET `/api/_plugins` | Anon | `PluginSdk.Runtime/PluginSdkServiceCollectionExtensions.cs` |
| GET `/api/{slug}/{contentType}`, `/api/{slug}/{contentType}/{itemSlug}` | Anon (dispatched through `PluginRouteTable`) | `Delivery/DeliveryEndpoints.cs` |
| GET `/api/{slug}/search` | Anon | `Delivery/SearchDeliveryEndpoints.cs` |
| GET `/api/tags` | Anon | `Delivery/TagDeliveryEndpoints.cs` |
| GET `/api/media/{assetId}/{variant}`, `/api/media/{assetId}/hls/{**file}` | Anon (capability = asset GUID) | `Delivery/MediaDeliveryEndpoints.cs` |
| GET `/api/openapi.json`, `/api/openapi.yaml`, `/api/openapi` (Scalar HTML) | Anon | `Delivery/OpenApiEndpoints.cs` |
| POST `/api/{slug}/collect`, POST `/api/collect`, GET `/api/analytics/status` | Anon, CORS any-origin | `Delivery/AnalyticsIngestEndpoints.cs` → NATS `analytics.events` |
| POST `/api/{slug}/forms/{formName}` | Anon, CORS any-origin (A) | `Forms/FormSubmissionEndpoints.cs` → email queue + `notify.raise` |
| GET `/api/{slug}/branding` | Anon, CORS any-origin GET | `Branding/BrandingEndpoints.cs` |
| GET `/api/{slug}/_config` (manifest `PublicConfigKeys` allow-list) | Anon | `Plugins/PluginConfigEndpoints.cs` |
| POST `/api/{slug}/register`, `/login`, `/refresh`; GET `/api/{slug}/me` | Anon / visitor JWT (HS256, audience `dcms.site:{tenantId}`, 15 min access, 30-day refresh) | `Visitors/VisitorAuthEndpoints.cs`, `VisitorTokenService.cs` |
| GET `/api/{slug}/chat/conversations/{cid}/messages` | Anon (capability = conversation GUID; verify) | `Chat/ChatEndpoints.cs` |
| GET `/api/{slug}/instagram-story` (ContentType const) | Anon; hops to admin-api with a dcms.social token | `Social/StoryDeliveryEndpoints.cs` |
| Hub `/hub/chat` | No `[Authorize]`: visitors are anonymous, agents present a platform JWT via `access_token` query | `Chat/ChatHub.cs`, `ChatBotResponder.cs` |

### 5.3 identity (AUTH_HOST; `/api/identity` also on PLATFORM_HOST)

| Route | Auth | File |
| --- | --- | --- |
| `GET/POST /connect/authorize`, `POST /connect/token`, `GET/POST /connect/userinfo`, `GET/POST /connect/logout` | OpenIddict passthrough | `Endpoints/AuthorizationEndpoints.cs` |
| `GET/POST /account/login`, `/account/register`, `/account/forgot-password`, `/account/reset-password`; `GET /account/external/google`, `/account/external/callback`; `POST /account/external/complete` | Anon, server-rendered HTML, DisableAntiforgery, `returnUrl` params | `Endpoints/AccountEndpoints.cs` |
| `/account/api/me` (GET, DELETE), `/password`, `/ssh-keys` (GET, POST, DELETE `{id}`) | Bearer (OpenIddict validation) | `Endpoints/AccountApiEndpoints.cs` |
| `/api/identity/users` (GET), `/users/{id}` (GET), `/users/{id}/lock\|unlock\|confirm-email` (POST), `/users/{id}/roles` (POST), `/users/{id}/roles/{role}` (DELETE) | Bearer + **role SuperAdmin** | `Endpoints/PlatformUserEndpoints.cs` |

OpenIddict discovery/JWKS endpoints are provided by the library (`/.well-known/openid-configuration`, JWKS).

### 5.4 platform-api (`/api/platform/...` on PLATFORM_HOST)

| Route | Auth | File |
| --- | --- | --- |
| GET `/api/platform/me` | Auth | `Authz/PlatformAuthzEndpoints.cs` |
| GET `/permissions/catalog`, GET `/roles`, PUT `/roles/{roleName}/permissions` | PP(platform:roles:manage) | same |
| GET `/overview`, `/growth` | PP(platform:overview:read) | `Reporting/PlatformOverviewEndpoints.cs` (reads `obs.*` views) |
| GET `/tenants` | PP(platform:tenants:read) | same |
| GET `/stores` | PP(platform:logs:read) | `Stores/PlatformStoreEndpoints.cs` |
| GET `/purge/loki` · POST `/purge/loki`, DELETE `/purge/loki/{requestId}`, POST `/purge/prometheus`, POST `/purge/docker-logs` | PP(logs:read) · PP(logs:purge) | `Purge/PlatformPurgeEndpoints.cs` → Loki, Prometheus, log-janitor |
| GET `/audit` | PP(platform:audit:read) | `Reporting/PlatformAuditEndpoints.cs` |
| GET `/health/signals` | PP(platform:observability:read) | `Observability/PlatformHealthEndpoints.cs` → Prometheus |
| `/certificates/**`, `/notifications/**`, `/tenants/{id}/suspend\|resume`, `/ops/analytics/prune` | PP(per route) → **delegated** to admin-api with `dcms.console` client-credentials token + audit propagation headers | `Delegation/DelegatedConsoleEndpoints.cs`, `AdminApiProxy.cs` |
| Hub `/api/platform/hub/console` | `[Authorize]` | `Realtime/PlatformHub.cs` |

### 5.5 ai-gateway (internal only)

| Route | Auth | File |
| --- | --- | --- |
| POST `/v1/chat` | JWT audience `dcms-ai-gateway` (scope `dcms.ai`) | `ChatEndpoints.cs` |
| POST `/v1/messages` (Anthropic Messages shape; bridged to OpenAI-compatible providers) | same | `MessagesEndpoints.cs`, `Providers/AnthropicOpenAiBridge.cs` |
| GET `/internal/ping` | same | `Program.cs` |

### 5.6 site-host, edge, workers

- site-host:
  - GET `/internal/tls-allowed?domain=`, GET `/internal/tls-hostnames` (internal; called by edge `TlsAllowList`)
  - ANY `/api/**`, `/hub/**` → content-api
  - GET `/{**path}` → MinIO sites bucket
- edge: `/.well-known/acme-challenge/{token}`, `/.edge/signin`, `/.edge/signout*`, `/.edge/denied`, `/health`, `/health/live`
- site-builder, media-worker, email-worker: `/`, `/health`, `/health/live` only

### 5.7 NATS JetStream topology (`Dcms.Shared.Contracts/Messaging/Streams.cs`, provisioned by `infra/nats/provision-streams.sh`)

| Stream | Subject | Publisher(s) | Consumer(s) |
| --- | --- | --- | --- |
| TENANCY | `tenant.created` | admin-api `TenantProvisioning` | platform-api `PlatformLiveUpdates` |
| TENANCY | `tenant.domain.verified` | admin-api Domain(Certificate)Endpoints | edge `DomainCertificateProvisioner`, `EdgeConfigInvalidator`; admin-api notifications |
| TENANCY | `tenant.suspended` / `tenant.resumed` | admin-api `TenancyEndpoints` | site-host `TenantStatusInvalidator`, platform-api live |
| TENANCY | `plugin.instance.changed` | admin-api `PluginInstanceEndpoints` | admin-api notifications (content-api cache invalidation: UNKNOWN whether it listens) |
| TENANCY | `membership.changed` | admin-api (invitations, tenancy, tenant admin, my-account, provisioning) | admin-api `MembershipChangedConsumer` (perm cache) |
| TENANCY | `edge.certificate.reissue-requested` | admin-api `ManagedCertificateEndpoints` | edge `ManagedCertificateInvalidator` |
| TENANCY | `platform.notification.raised` | admin-api `PlatformNotificationPublisher` | platform-api live |
| CMS | `content.published` / `content.unpublished` | admin-api `OutboxDispatcher` (+ ContentEndpoints, ScheduledPublishWorker, MetaFeedSyncService) | content-api `SearchIndexer`, `ContentCacheInvalidator`; admin-api notifications |
| MEDIA (work queue) | `media.process.image\|video\|audio` | admin-api `MediaExtensions` | media-worker consumers |
| MEDIA_EVENTS | `media.processed` / `media.failed` | media-worker | admin-api notifications |
| SITES (work queue) | `site.publish.requested.{staticfiles,staticprerender,reactapp}` (+ legacy `site.publish.requested`) | admin-api `SiteEndpoints` (publish, webhook, rebuild) | site-builder lanes |
| SITES_EVENTS | `site.published` / `site.build.failed` | site-builder | admin-api notifications + `SiteLiveUpdates`; site-host `SiteCacheInvalidator` |
| ANALYTICS | `analytics.events` | content-api collect | admin-api `AnalyticsConsumer` |
| CHAT | `chat.message.posted` | content-api `ChatHub` | admin-api `ChatFanoutConsumer` |
| EMAIL (work queue) | `email.send` | `NatsEmailQueue` (identity, admin-api, content-api) | email-worker |
| AUDIT | `audit.submitted` | `NatsAuditSink` (platform-api, site-builder, email-worker) | admin-api `AuditIngestConsumer` |
| AUDIT | `audit.recorded` | admin-api `AuditChainWriter` | admin-api `AuditLogProjector` |
| NOTIFY | `notify.raise` | content-api forms | admin-api `NotificationIngestConsumer` |

---

## 6. Frontend Architecture

### 6.1 apps/admin (tenant admin SPA)

- **Entry:** `src/main.tsx`
  - calls `adoptTenantFromUrl()`
  - builds a `QueryClient` (retry 1, no refetch on focus)
  - renders `ThemeProvider` → `QueryClientProvider` → `TooltipProvider` → `RouterProvider` → `Toaster`
  - loads `./lib/i18n` (en/cs)
- **Runtime config:** `src/runtime-config.ts` (`createRuntimeConfig` from `@dcms/core`). Values: `oidcAuthority`, `oidcClientId` (`dcms-admin-spa`), `adminApiBase` (`/api`), `contentApiBase` (`''`). They are injected at container start by `docker-entrypoint.sh`, which substitutes `__DCMS_*__` placeholders in `index.html`, with `VITE_*` env as the dev fallback.
- **Auth:** `src/auth.ts` → `createAuth` (oidc-client-ts, scope `openid profile email roles dcms.admin offline_access`, **`localStorage` user store**, automatic silent renew). `useAuth.ts` holds hook state.
- **Tenant selection:** `src/tenants.ts` keeps the tenant slug in `localStorage` and adds `X-Dcms-Tenant` via `adminHeaders`.
- **HTTP client:** `src/lib/api.ts` → `createApiClient({ base, headers: adminHeaders, renew: renewSilently })` from `packages/core/src/http.ts`. Provides `get/post/put/patch/del`, `uploadWithProgress`, `fetchObjectUrl`, and `ApiError`.
- **Permissions (UI):**
  - `src/lib/permissions.ts` (`Perm` constants mirror `PlatformPermissions`)
  - `src/app/routeGuards.ts`: `ROUTE_GUARDS`, `OPEN_ROUTES`, `SETTINGS_SECTIONS`
  - `RequirePermission` from `@dcms/ui/permissions`, fed by GET `/api/admin/me/permissions`
- **Shell:** `src/app/AppShell.tsx`, `Sidebar.tsx` / `nav.tsx` / `navApi.ts` (GET `/admin/navigation`), `Topbar.tsx`, `TenantSwitcher.tsx`, `CommandPalette.tsx`, `StorageNotice.tsx`.
- **Routing** (`src/routes.tsx`, TanStack code-based, lazy pages):

| Path | Page | Guard |
| --- | --- | --- |
| `/auth/callback` | OIDC callback | — |
| `/` | `features/dashboard/DashboardPage` | open |
| `/tenants` | `features/tenants/TenantsPage` | SuperAdmin |
| `/plugins` · `/marketplace` | `features/plugins/PluginsPage` (+ `MetaConnectionWidget`) · `features/marketplace/MarketplacePage` | plugins:manage |
| `/content` | `features/content/ContentPage` (TipTap rich text / markdown) | content:read |
| `/media` | `features/media/MediaPage`, `MediaUploader` | media:read |
| `/forms` | `features/forms/FormsPage` | content:read |
| `/sites` | `features/sites/SitesPage`, `StaticSitePage` (Mode C) | site:edit |
| `/sites/$siteId` | `features/sites/SiteWorkspace` → Mode A `features/builder/BuilderPage` (GrapesJS) or Mode B `features/ide/IdePage` (Monaco, `ide/preview` esbuild-wasm worker, `ide/agent`) + `features/site-source` (VFS, git, source control, live updates) | site:edit |
| `/analytics` | `features/analytics/AnalyticsPage` (recharts) | analytics:read |
| `/assistant`, `/assistant/$conversationId` | `features/assistant/AssistantPage` (tool-using agent, `tools.ts`) | site:edit |
| `/chat` | `features/chat/ChatPage` (SignalR `/hub/chat` as agent) | chat:read |
| `/notifications` | `features/notifications/NotificationsPage` (+ `useNotificationHub`) | open |
| `/invite/accept` | `features/invitations/InviteAcceptPage` | open |
| `/account` | `features/account/AccountPage` (identity `/account/api/*`, delete account) | open |
| `/settings` (layout) → `general` (Workspace), `members`, `roles`, `domains`, `ai`, `audit`, `api` (OpenAPI/Scalar, zip downloads) | `features/settings/*`, `workspace`, `members`, `roles`, `rbac`, `domains`, `ai`, `audit`, `openapi` | per section perm |
| `/workspace`, `/members`, `/roles`, `/domains`, `/ai`, `/audit`, `/openapi` | legacy redirects to `/settings/*` | — |

- **Feature modules not routed directly:**
  - `features/agent` (framework-agnostic agent runtime: modes, scope, transactions, model router, VFS port, prompt-injection classifier)
  - `features/builder/{ai,code,panels,plugins,workers}`
  - `features/ide/{agent,diagnostics,generated,panel,preview,types}`
  - `features/site-source`
  - `features/rbac`
  - `src/chat/api.ts`
- **State:** TanStack Query for server state. Zustand stores for builder, IDE and agent. Some `localStorage` for tenant and UI prefs.
- **Realtime:** `@microsoft/signalr` to `/api/hub/notifications`, `/api/hub/sites`, `/hub/chat`.
- **File upload/download:**
  - `MediaUploader.tsx` → POST `/admin/media` (multipart with progress)
  - `StaticSitePage.tsx` → POST `/admin/sites/{id}/upload`
  - `AuthedImage.tsx` / `fetchObjectUrl` → `/admin/media/{id}/content`
  - `OpenApiPage.tsx` → `api-client.zip`, `site-starter.zip`
  - audit export
- **Forms/validation:** RJSF (`components/SchemaForm.tsx`) drives plugin config from the manifest JSON Schema. zod validates builder specs.
- **Error/loading:** `@dcms/ui/errors` (+ tests), sonner toasts, per-query loading states. Error boundaries exist (commit `4957bb9` references one). Detailed placement: UNKNOWN.
- **Build/test:** `tsc -b && vite build`; `vitest run` (37 test files); custom Vite plugin `vite-plugin-palette-types.ts` (Mode B dependency-palette type hints from `packages/site-builder-toolchain`).
- **Container:** `apps/admin/Dockerfile` → nginx (`nginx.conf`: SPA `try_files`, a `/api/` location, no security headers visible).

### 6.2 apps/platform (operator console)

- **Entry:** `src/main.tsx`
- **Auth:** `src/auth.ts` (client `dcms-platform-spa`, scope `dcms.platform`)
- **Runtime config:** `platformApiBase`, `identityApiBase`, `adminBase`, `grafanaBase`
- **Clients:** `src/lib/api.ts` has two clients: `platformApi` (base platform-api) and an identity client (base `/api/identity`)
- **Routes** (`src/routes.tsx`, guarded by `RequirePermission` with platform keys):

| Path | Page | Backend |
| --- | --- | --- |
| `/` | `features/overview/OverviewPage` | platform-api `/overview`, `/growth` |
| `/tenants` | `features/tenants/TenantsPage` | platform-api `/tenants`, `/tenants/{id}/suspend\|resume` (delegated) |
| `/users` | `features/users/UsersPage` | identity `/api/identity/users/*` |
| `/audit` | `features/audit/AuditPage` | platform-api `/audit` |
| `/monitoring` | `features/monitoring/MonitoringPage`, `SignalsPanel` | platform-api `/health/signals`; Grafana links |
| `/storage` | `features/storage/StoragePage` | platform-api `/stores`, `/purge/loki`, `/purge/docker-logs` |
| `/access` | `features/access/AccessPage` | platform-api `/roles`, `/permissions/catalog`, `/me` |
| `/certificates` | `features/certificates/CertificatesPage` | platform-api `/certificates/*` (delegated) |
| `/notifications` | `features/notifications/*` | platform-api `/notifications/*` (delegated) |

- **Live:** `features/live/useConsoleHub.ts` → `/api/platform/hub/console`; `liveMap.ts` maps resource tags to query keys.

### 6.3 Shared packages

| Package | Role | Consumers |
| --- | --- | --- |
| `@dcms/core` | `createAuth` (oidc-client-ts), `createApiClient` (fetch, renew-on-401, ApiError), `createRuntimeConfig`, permission helpers, formatting | admin, platform, ui |
| `@dcms/ui` | Radix-based components, `Page`, shell, DataTable, `RequirePermission`, `live` helpers, theme, tour, `ConfirmDeleteDialog` | admin, platform |
| `@dcms/api-client` | Zero-dep tenant-site HTTP core (`createHttpCore`, `createTenantClient`, visitor auth, analytics collect) | Embedded into admin-api (`Dcms.AdminApi.csproj` EmbeddedResource) and shipped inside generated client zips; admin SPA imports it |
| `@dcms/site-components` | Embeddable chat widget (SignalR `/hub/chat`) | tenant sites (distribution path: UNKNOWN) |
| `@dcms/gjs-schema` | zod schemas for Mode A site/component/theme/placeholder specs | gjs-blocks, admin |
| `@dcms/gjs-parse` | HTML/CSS parse, format, lint, diff, usage analysis | admin builder, gjs-blocks (dev) |
| `@dcms/gjs-blocks` | GrapesJS block/kit registry, traits, thumbnails, render/runtime parity | admin builder |
| `@dcms/site-template-react` | Mode B templates (`shared/`, `templates/{blank,content,landing}`); `shared/src/api` is committed generated fixture code | Embedded into admin-api (`SiteTemplates.cs`); pinned by `TemplateFixtureTests` |
| `@dcms/site-builder-toolchain` | `package.json` dependency palette only (MUI, Radix, react-router…) — never installed | `apps/admin/vite-plugin-palette-types.ts`; sandbox image (UNKNOWN exact use) |

---

## 7. End-to-End Feature Flows

### 7.1 Sign-in (admin SPA)

1. The `@dcms/core` `createAuth.login()` redirects to identity `/connect/authorize` (code + PKCE, client `dcms-admin-spa`).
2. identity: `AuthorizationEndpoints.AuthorizeAsync` needs the Identity cookie. If absent, the browser goes to `/account/login` (`AccountEndpoints.cs`), where `SignInManager` sets the cookie (Data Protection key ring shared in Postgres `dataprotection` schema). Optional Google SSO: `/account/external/google` → callback → `/account/external/complete`.
3. The browser is redirected to `/auth/callback` in the SPA. `completeSignin()` calls `/connect/token` and stores the tokens in `localStorage`. Access token lifetime is 10 min, refresh 14 days (`Identity/Program.cs`).
4. The SPA calls GET `/api/admin/me/tenants` (NM) and chooses a tenant (`localStorage`). Subsequent calls send `Authorization: Bearer` and `X-Dcms-Tenant: <slug>`.
5. admin-api processes each call:
   - JwtBearer (issuer = `Identity:Issuer`, audience `dcms-admin-api`)
   - Finbuckle `TenantStore` (tenancy.tenants by slug)
   - `TenantMembershipMiddleware` (membership probe + suspension)
   - `PermissionAuthorizationHandler` → `TenancyPermissionResolver` (Redis `perm:{tenant}:{user}`, 5 min TTL; invalidated by `membership.changed`)

### 7.2 Content authoring → publish → delivery

1. **Author:** `ContentPage.tsx` → `features/content/api.ts` → POST/PUT `/api/admin/content`. `ContentEndpoints.cs` writes `cms.content_items` and `cms.content_versions` via `CmsDbContext` (tenant query filter). The audit interceptor writes to `audit.audit_outbox` in the same transaction.
2. **Publish:** POST `/content/{id}/publish` writes the `cms.content_outbox` row. `OutboxDispatcher` (claims with `FromSqlRaw`) then publishes `content.published`. Scheduled publishing works through `cms.scheduled_publishes` and `ScheduledPublishWorker`.
3. **Consumers:**
   - content-api `SearchIndexer` → `search.search_documents` (Postgres FTS + pg_trgm)
   - `ContentCacheInvalidator` → Redis
   - admin-api notification consumers → `notifications.*` + SignalR `/api/hub/notifications`
4. **Delivery:** a visitor on a custom domain goes Edge (catch-all) → site-host `ApiProxy` (`DomainResolver`: Host → tenancy.domains, suspended → 404, then sets `X-Dcms-Tenant`) → content-api `DeliveryEndpoints` → `PluginRouteTable.Find(plugin, contentType)` → `PublishedContentReader` (Redis cache) → `CmsDbContext`.

### 7.3 Site publish (Modes A/B/C)

1. **Trigger:**
   - UI: `site-source/git.ts` or `SitesPage` → POST `/api/admin/sites/{id}/publish` (P site:publish) or POST `/sites/{id}/builds` (rebuild).
   - Git: a push to the `release` branch in Forgejo → **anonymous** POST `/api/internal/git/webhook` (HMAC).
2. **Queue:** `SiteEndpoints` creates a `sites.site_builds` row and publishes `site.publish.requested.<mode>` (subject from `Subjects.SitePublishSubjectFor`). The message carries tenant facts (for example whether analytics is enabled) because site-builder cannot read `cms`.
3. **Build:** site-builder `SitePublishConsumer` (lane), running as DB role `dcms_sitebuilder` with a scoped MinIO account:
   - **Mode C:** reads the uploaded zip from MinIO, extracts (`ZipArchive`) and writes to the `sites` bucket under `ArtifactPrefix`.
   - **Mode A:** `StaticSiteAssembler` wraps committed HTML/CSS pages and adds `_dcms/hydrate.js` (embedded `Runtime/hydrate.js`).
   - **Mode B:** `ReactAppBuilder` clones the source, detects pnpm/yarn/npm, and runs `install --ignore-scripts` then the vite build. In prod this runs inside `docker run` of `dcms/site-build-sandbox` via `docker-socket-proxy` (`--network none` for the build step, scrubbed env, gVisor optional via `DCMS_BUILD_RUNTIME`). It uploads the artifacts and the build log.
4. **Result:** publishes `site.published` / `site.build.failed`. Consumers are site-host `SiteCacheInvalidator`, admin-api notifications, and `SiteLiveUpdates` → `/api/hub/sites` → `useSiteLiveUpdates.ts`.
5. **Serve:** site-host `SiteHostEndpoints` GET `/{**path}` → MinIO object (`Cache-Control: public, max-age=60`), optionally edge output cache.
6. **Certificates:**
   - Domain verify: POST `/api/admin/domains/{id}/verify` (DNS TXT via `DnsTxtLookup`) → `tenant.domain.verified`.
   - Issuance: edge `DomainCertificateProvisioner` asks site-host `/internal/tls-hostnames` → `CertesAcmeIssuer` (HTTP-01 through `AcmeChallengeStore` in Redis) → `edge.certificates`. Private key protection involves Vault Transit (edge has one Transit key; exact wrapping: see `CertificateStore.cs`).

### 7.4 Media upload → processing → delivery

1. `MediaUploader.tsx` → POST `/api/admin/media` (multipart, P media:write).
2. `MediaIngestService`: `ContentSniffer` type detection, then MinIO put, then the `media.media_assets` row, then publish `media.process.{image|video|audio}`.
3. media-worker: `ImageProcessingConsumer` (sanitize via `MediaSanitizer`/`SvgSanitizer`, WebP ladder via ImageSharp), `VideoProcessingConsumer` (`VideoTranscoder`, FFmpeg → HLS), `AudioProcessingConsumer`. It writes `media.media_variants` (+ audit in the same transaction) and publishes `media.processed`/`media.failed`.
4. Delivery: content-api GET `/api/media/{assetId}/{variant}` and `/hls/{**file}` (proxy-streaming from MinIO); admin preview via `/api/admin/media/{id}/content`.

### 7.5 AI assistant / IDE agent

1. The agent loop runs **in the browser**: `features/ide/agent/useAgentSession.ts`, `features/assistant/useAssistantSession.ts`, `features/agent/runtime.ts`. Tools edit the VFS (`site-source/vfs.ts`), content and media through ordinary admin-api endpoints (`assistant/tools.ts`).
2. Model calls: `ide/agent/client.ts` → POST `/api/admin/ai/messages` (P site:edit, audited).
3. admin-api `AiAgentEndpoints.MessagesProxy` wraps `{tenantId, userId, request}`, obtains a **dcms.ai** client-credentials token (`ServiceTokenClient`), and streams to ai-gateway `/v1/messages`.
4. ai-gateway `AiProviderResolver` precedence is user → tenant → platform default (`Ai:Defaults:*`). It decrypts the API key via Vault Transit (`TenantSecretsKey`), applies `AiQuota` (Redis with an in-process fallback), and uses `AnthropicProvider` or `OpenAiCompatibleProvider` (+ `AnthropicOpenAiBridge`). Usage is scanned by `AnthropicUsageScanner`.
5. Transcripts: `AiConversationEndpoints` → `ai.conversations/messages/runs`, pruned by `AiConversationRetentionWorker`.
6. BYOK: PUT `/api/admin/ai/user-credentials` (Transit encrypt, `ai.user_ai_settings`); tenant defaults via PUT `/api/admin/ai/settings`.
7. Visitor chatbot: content-api `ChatBotResponder` → ai-gateway `/v1/chat` using **the same `dcms-admin-api` client credentials** (compose `ServiceClient__ClientId: dcms-admin-api` on content-api).

### 7.6 Platform console operation (e.g. suspend tenant)

1. `apps/platform/src/features/tenants/api.ts` → POST `/api/platform/tenants/{id}/suspend`.
2. platform-api `DelegatedConsoleEndpoints` checks PP(platform:tenants:lifecycle), then `AdminApiProxy` calls admin-api `/api/admin/tenants/{id}/suspend` with a **dcms.console** token and audit propagation headers.
3. admin-api: `ServicePrincipalGuard` (endpoint has `AllowConsoleService`) → `PropagatedActorMiddleware` restores the operator → handler (`ConsoleCaller.Allowed`) updates `tenancy.tenants`, publishes `tenant.suspended`.
4. Effects: site-host `TenantStatusInvalidator` drops the domain cache (delivery 404); `TenantMembershipMiddleware` now 403s non-SuperAdmin admin calls; platform-api `PlatformLiveUpdates` pushes over `/api/platform/hub/console`.

### 7.7 Meta (Instagram/Facebook) feeds

1. The plugins page `MetaConnectionWidget` → GET `/api/admin/social/{provider}/connect` (P plugins:manage) → creates `social.meta_oauth_states` → redirect to Meta.
2. Meta redirects to **anonymous** GET `/api/admin/social/callback` (state lookup `IgnoreQueryFilters`, rate-limited). `MetaOAuthClient` token exchange, token Transit-encrypted → `social.meta_connections`.
3. `MetaSyncWorker`/`MetaFeedSyncService` → `MetaGraphClient` → `MetaMediaMirror` (downloads from the Meta CDN into media via `MediaIngestService`) → content items → `content.published`.
4. Stories (live): content-api `StoryDeliveryEndpoints` → admin-api stories endpoint with a **dcms.social** token.

### 7.8 Visitor accounts / forms / analytics (tenant site)

- Visitor auth: site JS (`@dcms/api-client` `VisitorAuthApi`) → `/api/{slug}/register|login|refresh|me` → `VisitorsDbContext` (`visitors.visitor_accounts`, hashed refresh tokens). JWT signed with `Visitor:SigningKey` (HS256).
- Forms: `/api/{slug}/forms/{formName}` → `forms.form_submissions`. Optional notification email is rendered by `FormNotificationEmail` → `email.send`, plus `notify.raise`. Preview writes go to the sandbox space when admin-api's preview proxy sets `X-Dcms-Sandbox` (`HeaderSandboxContext`, `ISandboxScoped`).
- Analytics: `/api/collect` → `analytics.events` subject → admin-api `AnalyticsConsumer` → `analytics.events` table + `daily_rollups` → `/api/admin/analytics` dashboard.

### 7.9 Backend ↔ frontend mismatch / orphan candidates (for audit — not confirmed defects)

| Item | Observation |
| --- | --- |
| PUT `/api/admin/sites/{id}/definition` | No caller found in `apps/`, `packages/` or `e2e/`. Memory notes say the v1 `SiteDefinition` editor was deleted, so this is a possible dead endpoint. The comment at `SiteEndpoints.cs:240` references `PATCH …/definition/files`, which does not exist (the actual route is `/ide/files`). |
| POST `/api/admin/sites/{id}/git/provision` | No SPA caller. `git.ts` says status auto-provisions on first read. |
| POST `/api/platform/purge/prometheus` | No console caller found (storage page calls `/purge/loki` and `/purge/docker-logs` only). |
| GET `/api/admin/ai/ping` | Diagnostic only; no caller. |
| `/api/admin/analytics/prune`, `/api/admin/tenants/{id}/suspend\|resume`, `/api/admin/platform/*` | Callers are platform-api delegation (server-to-server), not an SPA. The e2e fixtures still mock `/api/admin/platform/notifications` and `/certificates`, which may be stale after P3 (commit `f7338a3`). |
| GET `/api/admin/tenants`, POST `/api/admin/tenants` | Admin SPA `/tenants` page (SuperAdmin). The console also lists tenants via platform-api `/tenants` (obs views): two tenant listings exist. |
| `IPluginEndpointBuilder.MapGet/MapPost/Group` | Declared in the SDK, but `RecordingEndpointBuilder` throws `NotSupportedException`. Plugins can only declare list/get-by-slug; custom plugin endpoints are unimplemented. `MapDcmsPlugins` maps only `/api/_plugins`. |
| FE types | Hand-written TS interfaces per feature (`features/*/api.ts`) with no generated contract from the backend. Shape drift is possible everywhere; only e2e mocks and integration tests pin shapes. |
| Permission constants | `apps/admin/src/lib/permissions.ts` duplicates `PlatformPermissions`; `apps/platform` duplicates `PlatformConsolePermissions`. They are synchronised manually. |
| Scopes | `IdentitySeeder.SeedScopesAsync` and `options.RegisterScopes` in `Identity/Program.cs` must be kept in sync manually (comment documents a past miss). |
| `RlsConfigurator.TenantTables` | Hand-maintained. `AssertCoverage` (model-based, fails the migrate job) now exists, which supersedes the CLAUDE.md claim that nothing checks coverage. Note that `AssertRlsCoverage` enumerates DbContexts itself and must include new contexts. |
| `TenancyMigrator` | New DbContexts must be added in two lists (migrate + assert). |
| `.env.example` | Lists AppRole vars for 8 services but not `platform-api`, `edge` or `log-janitor`, which are present in `infra/vault/services.sh`. |

---

## 8. Authentication & Authorization Map

### 8.1 Token issuers and clients (identity, OpenIddict 7.5)

- **Flows enabled:** authorization code, refresh token, client credentials. Endpoints `connect/authorize|token|userinfo|logout`. Access tokens are **signed, not encrypted** JWTs (`DisableAccessTokenEncryption`), 10 min lifetime; refresh tokens 14 days.
- **Signing/encryption keys:** `OpenIddictCertificates.Load` from config (Vault). Development certificates are used in Development or with `Identity:AllowEphemeralKeys`.
- **Issuer:** `Identity:Issuer` (fixed). `Identity:AllowInsecureHttp` disables the transport-security requirement.
- **User store:** ASP.NET Core Identity (`DcmsUser`, `DcmsRole`, `identity` schema). Password min length 10, unique email, no confirmed-account requirement. Global roles are in `GlobalRoles` (`Domain/DcmsRole.cs`), including `SuperAdmin` and `Support`.
- **Google SSO:** only when `Authentication:Google:ClientId/Secret` are set.
- **Seeded clients** (`Seeding/IdentitySeeder.cs`, constants in `OpenIddictConstants.cs`):

| Client | Type | Grants / scopes | Default secret if unset |
| --- | --- | --- | --- |
| `dcms-admin-spa` | public, PKCE | code; `dcms.admin` | — |
| `dcms-platform-spa` | public, PKCE | code; `dcms.platform` | — |
| `dcms-admin-api` | confidential | client_credentials; `dcms.ai`, `dcms.social` | `dcms-admin-api-dev-secret` |
| `dcms-platform-api-service` | confidential | client_credentials; `dcms.console` | `dcms-platform-api-dev-secret` |
| `dcms-edge` | confidential, PKCE | code + refresh; email/profile/roles | seeded only if `Identity:Edge:Secret` set |
| `dcms-grafana` | retired | deleted by seeder if present | — |

- **SuperAdmin seed:** `Identity:SuperAdmin:Email/Password`, with defaults `admin@dcms.local` / `Admin!23456`.
- **Scopes → resources (audiences):**
  - `dcms.admin` → `dcms-admin-api`
  - `dcms.ai` → `dcms-ai-gateway`
  - `dcms.social` → `dcms-admin-api`
  - `dcms.platform` → `dcms-platform-api`
  - `dcms.console` → `dcms-admin-api`

### 8.2 Resource servers

- **JwtBearer:** `Dcms.Shared.Security/AuthServiceCollectionExtensions.cs`
  - `Auth:Authority`, `Auth:MetadataAddress` (internal discovery), `Auth:Issuer`, `Auth:Audience`, `Auth:RequireHttpsMetadata`
  - `MapInboundClaims=false`; claims `sub`, `name`, `role`, `email`, `scope`
  - JWT in query string (`access_token`) is accepted only for `/api/hub*` (admin-api) and `/hub*` (content-api)
- **Audiences in use:** admin-api and content-api use `dcms-admin-api`; ai-gateway uses `dcms-ai-gateway`; platform-api uses `dcms-platform-api`. Consequence to check: a `dcms.admin` user token is valid at content-api.
- **Identity's own APIs** use the OpenIddict validation scheme (`AccountApiEndpoints.PolicyName`, `PlatformUserEndpoints.PolicyName` + role SuperAdmin).

### 8.3 Authorization layers

| Layer | Mechanism | Location |
| --- | --- | --- |
| Tenant permission | `RequirePermission("x")` → policy `dcms.perm:x` → `PermissionAuthorizationHandler`: SuperAdmin role bypass, else `ITenantContext.TenantId` + `sub` → `IPermissionResolver` | `Shared.Security/Authorization/*`, `AdminApi/Tenancy/TenancyPermissionResolver.cs` |
| Tenant permission keys | `PlatformPermissions` (19 keys) + plugin keys `plugin:{id}:{action}` + per-site repo keys `repo:{siteId}:read\|write` | `Shared.Security/Permissions.cs` |
| Role data | `tenancy.tenant_roles`, `tenant_role_permissions`, `member_roles`, `tenant_memberships`; owner backfill `OwnerPermissionBackfill.cs` | `Shared.Data/Tenancy` |
| Tenant membership / suspension | Middleware runs before authorization for every request carrying a resolved tenant | `AdminApi/Tenancy/TenantMembershipMiddleware.cs` |
| Service principals | `ServicePrincipalGuard` rejects user-less tokens unless the endpoint declares `AllowServicePrincipal(scope)` | `AdminApi/Tenancy/ServicePrincipalGuard.cs` |
| Console callers | `ConsoleCaller.Allowed` = SuperAdmin OR a `dcms.console` service token; `RequireOperatorId()` uses the propagated actor | `AdminApi/Tenancy/ConsoleCaller.cs` |
| Platform permissions | `RequirePlatformPermission` → `dcms.pperm:` → `PlatformPermissionAuthorizationHandler` (SuperAdmin bypass; global roles → `platform.role_permissions`) | `Shared.Security/PlatformConsolePermissions.cs`, `PlatformApi/Authz/*` |
| Resource-level checks | Inside handlers: e.g. AI conversations `OwnerUserId == me`, notifications by recipient, `RequireUserId()`, site-scoped repo perms in `RepoAccessReconciler` | various endpoint files |
| Data isolation | **EF global query filters** on `TenantId` (primary guard) in `CmsDbContext`, `MediaDbContext`, `SitesDbContext`, `TenancyDbContext`, `SearchDbContext`, `FormsDbContext`, `VisitorsDbContext`, `ChatDbContext`, `SocialDbContext`, `NotificationsDbContext`; `IgnoreQueryFilters()` used for cross-tenant paths | `Shared.Data/*` |
| RLS (defense in depth) | Two permissive policies per tenant table: `tenant_isolation` keyed on `app.tenant_id`, `platform_scope` keyed on `app.scope = 'platform'`. Services on the owner connection bypass it; it applies to non-owner roles: `dcms_rls`, and `dcms_app`, which the services move onto one at a time in ADR 0015 phase 4 (media-worker, then ai-gateway). Audit partition upkeep and retention run through the owner's `SECURITY DEFINER` functions `audit.ensure_partitions` / `audit.drop_sealed_partition`, the only DDL `dcms_app` can cause. Work outside a tenant request declares itself with `RlsScope.Tenant(id)` (one tenant, the default) or `RlsScope.Platform()` (genuine scans); `TenantGucInterceptor` turns that and the request tenant into the GUCs when `Rls:Enforce` is on. `DCMS_TEST_RLS_ENFORCE=1` runs the AdminApi collection as `dcms_app`. ADR 0005 + ADR 0015. | `Shared.Data/Rls/`, `infra/postgres/init/01-rls.sql`, `06-app-role.sh` |
| DB least privilege | `dcms_sitebuilder` (sites, BYPASSRLS), `dcms_edge` (edge), `dcms_platform` (platform + obs), `dcms_grafana` (obs), `dcms_rls` (read-only test subject), `dcms_app` (NOBYPASSRLS runtime role; media-worker and ai-gateway so far) | `infra/postgres/init/02–06*.sh`, `PlatformRoleConfigurator.cs` |
| Edge gating | Cookie+OIDC; `SignedIn` / `SuperAdmin` policies on Grafana/Forgejo routes; identity headers `X-WEBAUTH-*` to upstreams | `Dcms.Edge/Auth/*`, `Transforms/IdentityHeaders.cs` |
| Visitor (tenant site end users) | HS256 JWT, issuer `dcms`, audience `dcms.site:{tenantId}` | `ContentApi/Visitors/VisitorTokenService.cs` |
| Anonymous-with-secret endpoints | Git webhook (HMAC), alerts webhook (shared secret), Meta OAuth callback (state token) | §5.1 |
| UI permission gating | `ROUTE_GUARDS` + `RequirePermission` (cosmetic; server enforces) | `apps/*/src/app/routeGuards.ts`, `packages/ui/src/permissions` |
| Forgejo credentials | identity mirrors each user into Forgejo (`ForgejoUserSync`, password outbox encrypted with Data Protection); admin-api reconciles repo collaborators (`RepoAccessReconciler`) | `Identity/Forgejo/*`, `AdminApi/Sites/Git/*` |

### 8.4 Service-to-service authentication

| Caller → callee | Credential |
| --- | --- |
| admin-api → ai-gateway | client_credentials `dcms-admin-api`, scope `dcms.ai` |
| content-api → ai-gateway | same client id/secret as admin-api (`ServiceClient__ClientId: dcms-admin-api`) |
| content-api → admin-api (stories) | same client, scope `dcms.social` |
| platform-api → admin-api | `dcms-platform-api-service`, scope `dcms.console`, + audit propagation headers |
| edge → site-host `/internal/tls-*` | **none** (network-internal) |
| site-host → content-api | **none**; sets `X-Dcms-Tenant` |
| admin-api → content-api (preview proxy) | **none**; sets tenant + sandbox headers |
| platform-api → Prometheus/Loki/log-janitor | none / Loki `X-Scope-OrgID: fake` / log-janitor secret from Vault (see `janitor.py`) |
| services → Vault | AppRole (`VAULT_ROLE_ID/SECRET_ID`) or `VAULT_TOKEN` (dev root `dcms-dev-root`; refused when `DCMS_REFUSE_DEV_VAULT`) |
| services → NATS | `nats.conf` defines user `app`; `Nats:Url` carries `app:${NATS_APP_PASSWORD}` in every deployed environment (base compose is unauthenticated for local dev only) |
| services → Postgres/Redis/MinIO | connection strings (Vault in deployed envs); Redis `requirepass ${REDIS_PASSWORD}` in the prod overlay, no auth in base compose (local dev) |
| admin-api → Forgejo | `Forgejo:Token`; identity → Forgejo `Forgejo:AdminToken` |

---

## 9. Data & Persistence Map

### 9.1 Database

- **One PostgreSQL 18 database** (`dcms`), one schema per context (`infra/postgres/init/00-schemas.sql`):
  - `identity`, `tenancy`, `plugins`, `cms`, `media`, `sites`, `search`, `analytics`, `chat`, `visitors`, `forms`, `ai`, `audit`, `social`, `notifications`, `edge`, `obs`
  - plus `platform` and `dataprotection`, created by EF migrations
- **Extensions:** `pg_trgm`, `pg_stat_statements`.
- **Migrations:** EF Core, per context, history table `__ef_migrations_history` inside each schema.
  - Locations: `src/Shared/Dcms.Shared.Data/Migrations/{Ai,Analytics,Audit,Chat,Cms,DataProtection,Edge,Forms,Media,Notifications,Platform,Search,Sites,Social,Visitors}` and `Migrations/` root (tenancy); `src/Services/Dcms.Identity/Migrations`.
- **Who migrates:** the `migrate` compose job (`admin-api --migrate-only`) runs `DcmsMigrationRunner` (`AdminApi/Tenancy/TenancyMigrator.cs`):
  1. RLS coverage assertion
  2. all contexts migrated in order
  3. `AuditSchemaConfigurator` (partitions/grants)
  4. `RlsConfigurator.ApplyAsync`
  5. `ObservabilityViewConfigurator` (`obs.*` views)
  6. owner-permission backfill

  `identity-migrate` runs identity migrations and seeds. Before both, `postgres-bootstrap` applies `infra/postgres/init/*` idempotently. Postgres advisory locks (`PostgresAdvisoryLock.cs`) serialise migrate/seed.
- **Design-time factories:** `CmsDbContextFactory`, `MediaDbContextFactory`, `SitesDbContextFactory`, `TenancyDbContextFactory`, `IdentityDbContextFactory`.

### 9.2 DbContexts

| Context | Schema | Entities (DbSets) | Tenant-filtered | Registered by |
| --- | --- | --- | --- | --- |
| `TenancyDbContext` | tenancy | Tenants, Domains, Memberships, TenantRoles, TenantRolePermissions, MemberRoles, Invitations | yes (except Tenants) | admin-api, content-api, site-host |
| `CmsDbContext` | plugins + cms | PluginInstances, ContentItems, ContentVersions, Outbox, ScheduledPublishes | yes (outbox/schedules exempt) | admin-api, content-api |
| `MediaDbContext` | media | Assets, Variants, Folders | yes | admin-api, content-api, media-worker |
| `SitesDbContext` | sites | Sites, Builds, Drafts | yes | admin-api, site-host, site-builder |
| `AiDbContext` | ai | Settings (tenant), UserSettings, Conversations, Messages, Runs | no ambient filter in ctor (explicit predicates; RLS list includes them) | admin-api, ai-gateway |
| `SearchDbContext` | search | Documents | yes | admin-api, content-api |
| `AnalyticsDbContext` | analytics | Events, DailyRollups | no ctor tenant param (explicit) | admin-api |
| `VisitorsDbContext` | visitors | Accounts, RefreshTokens | yes (+ sandbox) | admin-api, content-api |
| `ChatDbContext` | chat | Conversations, Messages | yes (+ sandbox) | admin-api, content-api |
| `FormsDbContext` | forms | Submissions | yes (+ sandbox) | admin-api, content-api |
| `SocialDbContext` | social | Connections, SyncStates, MediaMap, OAuthStates | yes | admin-api |
| `NotificationsDbContext` | notifications | Notifications, Recipients, PlatformNotifications, PlatformNotificationReads | yes (platform tables not) | admin-api |
| `AuditDbContext` | audit | Events, ChainHeads, ChainAnchors, Outbox | explicit | admin-api, content-api, identity, ai-gateway, site-host, media-worker |
| `PlatformDbContext` | platform | RolePermissions (+ keyless obs view reads) | n/a | admin-api (DDL), platform-api (as `dcms_platform`) |
| `EdgeDbContext` | edge | Certificates, AcmeAccounts, Routes, ManagedCertificates, ManagedCertificateAttempts, DataProtectionKeys | n/a | admin-api (DDL), edge (as `dcms_edge`) |
| `DataProtectionDbContext` | dataprotection | DataProtectionKeys | n/a | all services using `AddDcmsDataProtection` |
| `IdentityDbContext` | identity | ASP.NET Identity tables + OpenIddict tables + ForgejoSyncOutbox | n/a | identity |

Base type `TenantEntity` (`Shared.Data/TenantEntity.cs`). Sandbox scoping via `ISandboxScoped` + `ISandboxContext` (`Shared.Data/Sandbox`).

### 9.3 Raw SQL / non-LINQ data access

| File | Purpose |
| --- | --- |
| `Shared.Data/Cms/ContentListQueries.cs` | Paged content listing (hand-built SQL, bound parameters per comment) |
| `Shared.Data/Cms/TagQueries.cs` | Tag aggregation/filtering (`CommandText = sql` ×3) |
| `PlatformApi/Reporting/ObservabilityQuery.cs` | `obs.*` view reads |
| `AdminApi/Cms/OutboxDispatcher.cs`, `ScheduledPublishWorker.cs`, `Notifications/InvitationExpiryWorker.cs`, `Identity/Forgejo/ForgejoSyncWorker.cs` | `FromSqlRaw` claim queries (`FOR UPDATE SKIP LOCKED` style — verify) |
| `Identity/Endpoints/AccountApiEndpoints.cs:209` | `SqlQueryRaw<int>` |
| `Shared.Data/Rls/RlsConfigurator.cs`, `Audit/AuditSchemaConfigurator.cs`, `Audit/AuditRetention.cs`, `Observability/ObservabilityViewConfigurator.cs`, `Platform/PlatformRoleConfigurator.cs` | DDL/grants via `ExecuteSqlRawAsync` with interpolated identifiers from constants; `pg_roles` lookups with interpolated role names |
| `Shared.Data/PostgresAdvisoryLock.cs` | advisory locks |
| `Shared.Data/Observability/ObservabilityViews.sql.cs` | view DDL |
| Stored procedures | none found |

### 9.4 Transactions, outboxes, caching

- **Transactional outbox:** `cms.content_outbox` (content events) and `audit.audit_outbox` (via EF interceptor, `AuditOutboxInterceptor`, `AuditBulkCommandInterceptor` for `ExecuteUpdate/Delete`).
- **Audit hash chain:** HMAC chain (`AuditChainAppender`, `AuditChainKey` from `AUDIT_CHAIN_KEY`), sealing (`AuditChainSealer`), verification (`AuditChainVerifier`, GET `/api/admin/audit/verify`), gap detection, monthly partitions and retention (ADR 0007).
- **Explicit transactions:** e.g. the role PUT uses `ExecuteDelete` + insert in a tx (memory note on the EF remove/re-add bug); others per handler (not enumerated).
- **Caching:**
  - Redis `ICacheService` (permission sets, published content, AI quota, ACME challenges)
  - `IMemoryCache` (site-host domain resolution, edge)
  - edge OutputCache (off by default)
  - SignalR backplanes with channel prefixes `dcms-notify`, `dcms-chat`, `dcms-console`
- **Connection config:** `ConnectionStrings:Postgres` (default fallback `Host=localhost;…;Password=dcms-dev` hardcoded in `TenancyServiceCollectionExtensions` and `Identity/Program.cs`), `ConnectionStrings:Redis`, `Nats:Url`, `Storage:*`.
- **Object storage (MinIO):** buckets configured by `StorageOptions` (e.g. `SitesBucket`) and `infra/minio/init.sh` (scoped `dcms-sitebuilder` account).
- **Data Protection:** shared key ring in Postgres (`dataprotection` schema), wrapped with Vault Transit in production (`DataProtection:ProtectWithTransit`, on in `docker-compose.prod.yml`'s env anchor, key `dcms-dataprotection`). The edge keeps its own ring in `edge.data_protection_keys`, always wrapped, on its own key `dcms-edge-dataprotection`.

---

## 10. External Integrations

| Integration | Purpose | Code path | Credentials / config |
| --- | --- | --- | --- |
| Anthropic API | IDE agent, assistant, generation, chatbot | `AiGateway/Providers/AnthropicProvider.cs`, `MessagesEndpoints.cs` | BYOK per user/tenant (Transit ciphertext in `ai.*`), platform default `Ai:Defaults:ApiKey` |
| OpenAI-compatible (OpenAI, Ollama, LM Studio, arbitrary base URL) | Same, other providers | `OpenAiCompatibleProvider.cs`, `AnthropicOpenAiBridge.cs`; base URL validation `Shared.Data/Ai/AiBaseUrl.cs` | User/tenant `BaseUrl` |
| Let's Encrypt (ACME) | Per-domain HTTP-01 + platform wildcard DNS-01 | `Edge/Certificates/CertesAcmeIssuer.cs`, `CertificateProvisioner.cs`, `ManagedCertificateProvisioner.cs` | `Edge:Certificates:AcmeDirectory` (staging default), `ACME_EMAIL`; account in `edge.acme_accounts` |
| Cloudflare DNS API | DNS-01 TXT records | `Edge/Certificates/Dns/CloudflareDnsChallengeWriter.cs`, `DnsPropagationWaiter.cs` | token in Vault `secret/dcms/edge` |
| Pebble / challtestsrv | Local ACME testing | compose profile `acmetest`, `ChallTestSrvDnsChallengeWriter.cs` | — |
| Public DNS (TXT verify) | Domain ownership | `AdminApi/Tenancy/DnsTxtLookup.cs` (DnsClient) | — |
| Google OAuth | SSO | `Identity/Program.cs`, `AccountEndpoints.cs` | `GOOGLE_CLIENT_ID/SECRET` |
| Meta Graph API / OAuth | Instagram/Facebook feeds and stories | `AdminApi/Social/*` (`MetaOAuthClient`, `MetaGraphClient`, `MetaMediaMirror`, workers) | `Social:*` options (`MetaSocialOptions`), tokens Transit-encrypted |
| SMTP relay (MailKit) | All email (password reset, invitations, form notifications, alerts) | `Shared.Messaging/Email/SmtpEmailSender.cs` in email-worker only | `EMAIL_*` |
| Forgejo | Site git repos, webhooks, user mirroring, SSH keys | `AdminApi/Sites/Git/ForgejoClient.cs`, `SiteGitService*.cs`, `RepoAccessReconciler.cs`; `Identity/Forgejo/*`; `AccountApiEndpoints` (ssh-keys) | `FORGEJO_TOKEN`, `FORGEJO_ADMIN_TOKEN`, `FORGEJO_WEBHOOK_SECRET` |
| `git` CLI | Local merge / conflict resolution | `SiteGitService.LocalMerge.cs` (`Process.Start("git")`) | — |
| Docker Engine (via socket proxy) | Mode B sandbox builds | `SiteBuilder/ReactAppBuilder.cs` (`docker run`), compose `docker-socket-proxy` (prod), `docker-socket-proxy-ro` (base, read-only for Alloy/log-janitor) | `DOCKER_HOST` |
| npm registry (pnpm/yarn/npm) | Mode B dependency install | `ReactAppBuilder.cs` (install step has network; build step `--network none`) | — |
| MinIO | Media, site artifacts, bundles, build logs | `Shared.Storage/MinioObjectStorage.cs` | `Storage:*` |
| Vault | Config secrets (KV v2 `secret/dcms/shared` + `secret/dcms/{service}`), Transit (tenant secrets, social, edge, data protection) | `Shared.Vault/*` | AppRole per service |
| Seal Vault (VPSM) | Transit auto-unseal | `infra/vault/server/seal-transit.hcl`, `docs/seal-vault.md` | `VAULT_TRANSIT_SEAL_TOKEN` |
| Grafana | Dashboards; posts alerts to admin-api | `infra/observability/grafana`, `AdminApi/Observability/AlertEndpoints.cs` | `ALERT_WEBHOOK_SECRET` |
| Prometheus / Loki / Tempo / Alloy | Telemetry storage; console reads/purges | `PlatformApi/Observability/{PrometheusClient,LokiClient}.cs`, `Purge/*` | internal URLs |
| log-janitor (Python) | Truncates container JSON logs on request | `infra/log-janitor/janitor.py`, `PlatformApi/Observability/LogJanitorClient.cs` | Vault secret |
| GitLab Container Registry | Images | `.gitlab-ci.yml` | CI job token |
| FFmpeg | Video/audio transcoding (HLS) | `MediaWorker/VideoTranscoder.cs`, `AudioTranscoder.cs` | binary in image |
| Payments | **None found** | — | — |

---

## 11. Infrastructure & Deployment

### 11.1 Environments / topology (from docs + memory notes; verify)

- **VPSM:** GitLab (`gitlab.highgeek.eu`), the seal Vault, this development box.
- **vps1 = dev:** deployed from `master` pushes; the CI environment URL is `admin.dev.highgeek.eu`.
- **New VPS = prod:** deployed from `v*` tags with a manual job; URL `admin.highgeek.eu`.

### 11.2 Compose

| File | Role |
| --- | --- |
| `docker-compose.yml` | Base. Anchors `x-logging`, `x-otel-env`, `x-service-env` (dev creds + `VAULT_TOKEN: dcms-dev-root`), `x-service-defaults` (depends_on + `/health/live` healthcheck). 38 services, including profiles `migrate` (postgres-bootstrap, migrate, identity-migrate), `sandbox` (site-build-sandbox image), `acmetest` (pebble, challtestsrv), `logjanitor`. |
| `docker-compose.override.yml` | Dev ports and settings (auto-loaded locally) |
| `docker-compose.prod.yml` | Production: pinned behaviour, `docker-socket-proxy` (privileged, CONTAINERS/IMAGES/NETWORKS/POST enabled), sandboxed Mode B builds (`DCMS_BUILD_REQUIRE_SANDBOX`, `DCMS_BUILD_NETWORK_BUILD: none`), `*prod-env` anchor |
| `docker-compose.vps.yml` | Host overlay: edge on 80/443, nats-surveyor, AppRole env, Forgejo, etc. |

**Dev infra images use floating tags:** `hashicorp/vault:latest`, `quay.io/minio/minio:latest`, `mc:latest`, `natsio/nats-box:latest`, `axllent/mailpit:latest`, `tecnativa/docker-socket-proxy:latest`, `dcms/site-build-sandbox:latest`.

**Ports** (base): Forgejo `127.0.0.1:3000` and `2222:22` (SSH on all interfaces). Other host ports come from the override/vps overlays.

### 11.3 Dockerfiles

- One per service: `src/Services/*/Dockerfile`.
- Sandbox image: `src/Services/Dcms.SiteBuilder/sandbox.Dockerfile`.
- SPAs: `apps/admin/Dockerfile`, `apps/platform/Dockerfile` (nginx + `docker-entrypoint.sh` runtime config substitution + `nginx.conf`).
- `infra/log-janitor/Dockerfile`.
- `.dockerignore` at root.

### 11.4 CI/CD (`.gitlab-ci.yml`)

| Stage | Job | What | When |
| --- | --- | --- | --- |
| build | `dotnet` | `dotnet build Dcms.sln -c Release` + `dotnet test Dcms.sln` (JUnit). Integration tests self-skip without Docker. | all pipelines |
| build | `frontend` | `pnpm install --frozen-lockfile && pnpm -r build` (**no `pnpm test`, `pnpm lint` or `pnpm e2e`**) | all |
| test | `integration` | `dotnet test tests/Dcms.IntegrationTests` with docker:27-dind | MR + default branch |
| images | `build-image` (matrix of 14) | build/push `$CI_REGISTRY_IMAGE/<svc>:<sha>` and `:dev`, `resource_group` lanes a/b/c | default branch |
| promote | `promote-images` | retag `<sha>` → `<vX>` + `stable` | `v*` tags |
| deploy | `deploy:dev` | `scripts/ci/resolve-digests.sh` → `docker-compose.images.yml`; `scripts/ci/deploy-remote.sh --env dev` over SSH | default branch |
| deploy | `deploy:prod` | same with `--env prod`, `needs: promote-images` | `v*` tags, **manual** |

### 11.5 Host deploy (`scripts/deploy.sh`, run remotely)

1. Preflight: compose files per env, Vault AppRole env presence, `--check` mode.
2. Optional pull/build (serial).
3. Vault seal check.
4. `infra/vault/apply.sh [--seed]` using the host's `dcms-ops` AppRole (policies in `infra/vault/policies/*.hcl`, service list `infra/vault/services.sh`).
5. Fetch the Mode B sandbox image.
6. Provisioning jobs: NATS streams (`nats-init` → `provision-streams.sh`), MinIO (`minio-init` → `init.sh`).
7. Migration jobs (`--profile migrate`: postgres-bootstrap, migrate, identity-migrate).
8. Guard against recreating a sealed Vault (dry-run diff).
9. `compose up -d --remove-orphans`.
10. Force-recreate services with bind-mounted configs.
11. Health gate per service.
12. Record digests for `--rollback`.

Retired paths: `run-deploy*.sh` and tar-sync (per CLAUDE.md; these scripts are not in the tree).

### 11.6 Reverse proxy & TLS

- `Dcms.Edge` is the only ingress (Caddy removed, ADR 0010).
- Certificates: per-domain HTTP-01, platform wildcard via DNS-01 (ADR 0011).
- Store: `edge.certificates`; renewal: `CertificateRenewalService`; preflight: `EdgeTlsPreflight`.
- Allow-list: site-host `/internal/tls-allowed`.

### 11.7 Frontend deployment

- SPAs are built in Docker (Vite) and served by nginx containers `admin-spa` / `platform-spa`, behind the edge on `ADMIN_HOST` / `PLATFORM_HOST`.
- Runtime config is substituted into `index.html` at container start.

### 11.8 Observability

- Pipeline: OTLP gRPC → Alloy → Prometheus (metrics + `rules/`), Loki (logs), Tempo (traces) → Grafana (provisioned dashboards/datasources, `dcms_grafana` DB role reading `obs.*`).
- Scripts: `scripts/obs-smoke.sh`, `obs-disk-check.sh`, `obs-store-usage.sh`; `store-usage` container; `nats-exporter`.
- Details: ADR 0008, `infra/observability/README.md`.

---

## 12. Testing Architecture

### 12.1 .NET

| Project | Style | Infrastructure | Areas (file count / approx test attrs) |
| --- | --- | --- | --- |
| `tests/Dcms.UnitTests` | Pure unit tests (xUnit v3, NSubstitute, AwesomeAssertions) | none | Ai (2/34: `AiQuota`, `AnthropicOpenAiBridge`), Audit (5/42), Data (2/11: `AiBaseUrl`, `RlsCoverage`), Edge (12/70: provisioner, gating, http plane, resilience, Grafana cookies, header scrubbing, identity headers, internal identity handler, route table), Hosting (1/3), Kernel (1/3), Media (1/11), Messaging (3/10), Platform (2/15: health signal shape, Loki purge guard), Security (1/10: platform permission authz), Telemetry (2/12), Vault (1/4) |
| `tests/Dcms.PluginSdk.Tests` | Unit | none | Branding, Events, Forms, Roster, MetaFeed plugins; marketplace metadata; `OpenApiAssembler`; `PluginRegistry`; `PluginRouteTable`; public config (59 tests) |
| `tests/Dcms.IntegrationTests` | `WebApplicationFactory<Program>` + Testcontainers (Postgres, Redis, NATS, MinIO); `DockerFact` / `FfmpegFact` self-skip | `TestPostgres.cs`, `TestMinio.cs`, fixtures `AdminApiFixture`, `IdentityAppFixture`, `ContentFlowFixture`, `SitePublishFixture`, `TestAuthHandler` (fake auth), `MetaStubServer`, `FakeTransitEncryptor`, Respawn | Ai (3/14), ApiClientGen (5/21, incl. `TemplateFixtureTests`), Audit (7/33), Chat (1/1), Cms (4/16), Console (2/11), Edge (6/46), Hardening (1/1 `RlsIsolationTests`), Identity (5/19 OAuth contract, client seed convergence), Media (3/3), Notifications (7/23), Observability (3/7), Plugins (2/11), Security (1/4 `PermissionCoverageTests`), Sites (4/35), Social (7/27), Tenancy (3/3 `TenancyIsolationTests`), Visitors (1/4), plus `InfrastructureSmokeTests.cs` |

CLAUDE.md: the full integration suite OOMs on the dev box; run it filtered.

### 12.2 Frontend

- **Vitest (jsdom + Testing Library)** test files:

| Package | Test files |
| --- | --- |
| `apps/admin` | 37 (agent runtime, route guards, IDE preview broker/bridge/problems, …) |
| `apps/platform` | 2 |
| `packages/ui` | 20 |
| `packages/gjs-blocks` | 15 (roundtrip, runtime parity, render, drop…) |
| `packages/gjs-parse` | 6 |
| `packages/gjs-schema` | 5 |
| `packages/core` | 3 (`http`, `permissions`, `format`) |
| `packages/api-client`, `site-components`, `site-template-react` | 0 |

- **Playwright e2e** (`playwright.config.ts`, `e2e/`): starts both Vite dev servers and mocks the backend (`e2e/fixtures/api.ts`, `oidc.ts`, `hub.ts`, `aiStream.ts`, `dnd.ts`, `data.ts`).
  - Specs: admin (a11y with axe, content, ideAgent, ideTemplates, live, marketplace, media, permissions, settings, shell, signin), platform (a11y, console, live), mobile (responsive).
  - **Not run in CI.**
- **Load:** `loadtest/` (k6 scenarios + `run.sh`, `collect/`, `findings/`, `runs/`).
- **AI benchmark:** `scripts/ai-bench`.

### 12.3 Apparent coverage by component (inferred from test names; not measured)

| Component | Evidence of tests | Apparent gaps |
| --- | --- | --- |
| Edge (routing, headers, certs) | Strong (unit 70 + integration 46) | ACME against the real CA (by design) |
| Audit chain | Strong | — |
| Identity OAuth / seeding | Moderate (19) | Account pages (login/register/reset/SSO flows), `PlatformUserEndpoints`, Forgejo sync |
| Tenancy isolation | 3 tests + RLS raw test (1) + coverage assertion | Per-endpoint cross-tenant tests appear sparse |
| Permissions | `PermissionCoverageTests` (4), platform authz unit (10) | Membership middleware / service principal guard beyond Console tests |
| Sites publish | 35 | Git service/webhook HMAC, `StaticBundleBuilder` zip handling, `ReactAppBuilder` sandbox |
| Media | 3 integration + 11 unit | Upload endpoint validation, SVG sanitizer depth |
| Content delivery / CMS | 16 | Forms submit, visitor auth endpoints (only token service), search, branding, `_config` |
| AI | 14 + 34 | Messages proxy streaming, provider resolver precedence, base URL SSRF |
| Social/Meta | 27 | — |
| platform-api | Console delegation 11 + unit 15 | Purge endpoints (guard unit only), overview SQL |
| site-host | Indirect (sites fixtures) | `DomainResolver` suspension, API proxy header rewriting |
| email-worker | `NatsEmailQueueTests` | SMTP sender |
| content-api ChatHub | 1 (chat console) | Hub method authorization for agent vs visitor |
| Frontend admin | 37 vitest + 11 e2e specs | Many feature pages without unit tests; e2e is mock-only |
| api-client / site-components / templates | TemplateFixtureTests (C#) typecheck only | Runtime behaviour |

---

## 13. Important Configuration

### 13.1 Configuration flow

1. `appsettings.json` per service holds minimal defaults: `Auth:*`, `ServiceClient:*`, `Services:AiGateway`, `Platform:SeedRolePermissions`. There are no `appsettings.{Environment}.json` files.
2. **Environment variables** (compose anchors, `.env`) use `__` → `:` key mapping.
3. **Vault KV v2** (`Dcms.Shared.Vault/VaultConfigurationSource.cs`) loads `secret/dcms/shared` then `secret/dcms/{service}`, mapping `__` in keys to `:`. Refusal messages on 403/503 fail startup.
4. Hardcoded fallbacks in code, e.g. the Postgres connection string, `http://localhost:500x` service URLs, dev CORS origins, SuperAdmin defaults, client secrets.

### 13.2 Key configuration surfaces

| Key / var | Consumer | Notes |
| --- | --- | --- |
| `ConnectionStrings:Postgres/Redis`, `Nats:Url`, `Storage:*` | all | Scoped per service in prod (edge, site-builder, platform-api use their own roles) |
| `Auth:Authority/MetadataAddress/Issuer/Audience/RequireHttpsMetadata` | resource servers | Issuer must equal `Identity:Issuer` |
| `ServiceClient:TokenEndpoint/ClientId/ClientSecret` | admin-api, content-api, platform-api | content-api reuses admin-api's client |
| `Services:AiGateway/ContentApi/AdminApi` | service URLs | — |
| `Identity:Issuer`, `Identity:SigningCertificate`, `Identity:EncryptionCertificate`, `Identity:AllowEphemeralKeys`, `Identity:AllowInsecureHttp`, `Identity:Migrate`, `Identity:Seed`, `Identity:SuperAdmin:*`, `Identity:Spa:*`, `Identity:PlatformSpa:*`, `Identity:AdminApiService:Secret`, `Identity:PlatformApiService:Secret`, `Identity:Edge:*` | identity | — |
| `Authentication:Google:*` | identity | optional |
| `Cors:AllowedOrigins` | identity | — |
| `Visitor:SigningKey`, `AccessTokenMinutes`, `RefreshTokenDays` | content-api | prod guard |
| `Forgejo:BaseUrl/Token/AdminToken/WebhookSecret/Enabled` | admin-api, identity | prod guard on webhook secret |
| `Alerting:WebhookSecret`, recipients | admin-api | prod guard |
| `Social:SyncEnabled`, `Social:*` (Meta app) | admin-api | — |
| `Tenancy:Migrate`, `Tenancy:ApplyRls`, `Observability:ApplyViews` | admin-api migrator | — |
| `Rls:Enforce` | admin-api, content-api, site-host, media-worker, ai-gateway | `true` on media-worker and ai-gateway, set beside its `dcms_app` connection string; `false` elsewhere until ADR 0015 phase 4 moves each service |
| `DataProtection:ProtectWithTransit` | identity, content-api, ai-gateway, admin-api | `true` (prod env anchor) |
| `Ai:Defaults:Provider/BaseUrl/ApiKey/Model` | ai-gateway | — |
| `Edge:*` (AdminHost, PlatformHost, AuthHost, GrafanaHost, GitHost, Certificates:TlsEnabled/AcmeDirectory/AcceptInsecureAcmeDirectory/ContactEmail, Auth:Authority/InternalAuthority/ClientSecret, RateLimiting:*, Cache:*, Dns:*) | edge | — |
| `Observability:PrometheusUrl/LokiUrl/LogJanitorUrl` | platform-api | — |
| `DCMS_BUILD_SANDBOX_IMAGE`, `DCMS_BUILD_REQUIRE_SANDBOX`, `DCMS_BUILD_WORK_DIR`, `DCMS_BUILD_NETWORK_BUILD`, `DCMS_BUILD_RUNTIME`, `DOCKER_HOST` | site-builder | — |
| `VAULT_ADDR`, `VAULT_TOKEN`, `VAULT_ROLE_ID`, `VAULT_SECRET_ID`, `DCMS_REFUSE_DEV_VAULT` | all Vault users | — |
| `OTEL_EXPORTER_OTLP_ENDPOINT/PROTOCOL` | all | — |
| `AUDIT_CHAIN_KEY` | admin-api audit | — |
| SPA runtime: `DCMS_OIDC_AUTHORITY`, `DCMS_OIDC_CLIENT_ID`, `DCMS_ADMIN_API_BASE`, `DCMS_CONTENT_API_BASE` (+ platform equivalents) / `VITE_*` | SPAs | — |

### 13.3 Other configuration files

- `infra/nats/nats.conf`
- `infra/vault/server/config.hcl`, `seal-transit.hcl`, `policies/*.hcl`
- `infra/observability/**` (alloy `config.alloy`, `prometheus.yml` + rules, `loki.yaml`, `tempo.yaml`, `grafana.ini`, provisioning)
- `infra/acme-test/pebble-config.json`
- `apps/*/nginx.conf`, `apps/*/vite.config.ts`, `vitest.config.ts`
- `eslint.config.js`, `.editorconfig`, `.gitattributes`, `.prettierrc.json`

---

## 14. Potential Audit Hotspots

Audit targets only. None of these is asserted to be a vulnerability.

### Authentication & tokens
1. OIDC tokens (access + refresh, `offline_access`) are stored in **`localStorage`** by both SPAs (`packages/core/src/auth.ts`). Consider XSS blast radius, combined with #20.
2. Hardcoded dev defaults that production must override:
   - SuperAdmin `admin@dcms.local` / `Admin!23456`
   - `dcms-admin-api-dev-secret`, `dcms-platform-api-dev-secret`
   - Postgres `dcms-dev`, `dcms-dev-root` Vault token, MinIO `dcms-dev-secret`
   - Check which have production guards (Visitor key, webhook, alert, identity certificates do) and which do not (**seeded client secrets, SuperAdmin password**).
3. `Identity:AllowEphemeralKeys` and `Identity:AllowInsecureHttp` escape hatches.
4. identity account pages: `returnUrl` handling on login/register/forgot/reset/external flows (open redirect); `DisableAntiforgery` on all form POSTs (CSRF / login CSRF); account enumeration; password reset token handling; Google account linking (`/account/external/complete`).
5. `/connect/*` passthrough handlers (`AuthorizationEndpoints.cs`): claim destinations, role claims in tokens, consent type.
6. Visitor HS256 tokens (`VisitorTokenService`): a single platform-wide key; tenant binding only via audience; refresh-token rotation/reuse detection.
7. JWT `access_token` in the query string for hubs (logging/Referer exposure; path scoping).
8. content-api and admin-api share audience `dcms-admin-api`. A tenant user token is accepted by content-api and vice versa.

### Authorization / multi-tenancy
9. Bare `RequireAuthorization()` endpoints on admin-api: `/tenants` (GET/POST), `/permissions/catalog`, `/plugins/catalog`, `/openapi.json`, `api-client.zip`, `site-starter.zip`, `/notifications/*`, `/platform/*`, stories. Verify in-handler SuperAdmin/`ConsoleCaller`/tenant checks. (GET/POST `/api/admin/tenants` were checked in this phase: both return `Forbid()` unless `me.IsSuperAdmin`. The rest are unverified.)
10. `TenantMembershipMiddleware` passes through when the tenant header does not resolve or when `AllowNonMemberTenant` is set. Review every NM endpoint.
11. The SuperAdmin role bypasses all tenant permission checks and suspension.
12. Every `IgnoreQueryFilters()` usage (cross-tenant paths: Meta OAuth callback, invitations accept, `/me/tenants`, workers, site-builder).
13. RLS does not yet apply to app connections: they are the table owner (ADR 0005), and ADR 0015 is the staged change that ends that. EF query filters are still the only runtime isolation. DbContexts without a ctor tenant filter (`AiDbContext`, `AnalyticsDbContext`, `AuditDbContext`, `PlatformDbContext`) rely on explicit predicates.
14. Sandbox scoping via the `X-Dcms-Sandbox` header (`HeaderSandboxContext`). Confirm it cannot be set on the public plane beyond edge scrubbing (site-host path, admin host `/hub`).
15. Per-site repo permissions (`repo:{siteId}:*`) and `RepoAccessReconciler` (auto-granting repo access to site editors).
16. Permission cache (5 min Redis TTL) and invalidation on role/permission edits, not only membership changes.
17. Delegated console calls: `ServicePrincipalGuard`, `PropagatedActorMiddleware` (trusting propagated actor headers from a service token), `ConsoleCaller.RequireOperatorId`.
18. The content-api ChatHub has no `[Authorize]`; verify agent-vs-visitor authorization per hub method and conversation GUID capabilities (`/api/{slug}/chat/conversations/{cid}/messages`).
19. The platform role model: `platform:roles:manage` could grant other platform keys; identity keeps SuperAdmin minting behind the role (see comments). Verify no escalation.

### Untrusted code / content execution
20. **IDE preview iframe** `sandbox="allow-scripts allow-same-origin …"` with `srcDoc` (`apps/admin/src/features/ide/PreviewPane.tsx:51`). Tenant-authored and AI-authored code may run in the admin origin, with access to `localStorage` tokens. The GrapesJS canvas iframe needs the same review.
21. **Mode B builds** (`ReactAppBuilder.cs`): the install step has network access; untrusted `package.json`/lockfiles; the `docker-socket-proxy` (prod) enables CONTAINERS+IMAGES+NETWORKS+POST, which is effectively root-equivalent if the site-builder is compromised; the dev path runs unsandboxed; gVisor is optional; resource limits and timeouts.
22. Zip handling: `StaticBundleBuilder` (upload) and `SitePublishConsumer.ExtractStaticBundleAsync` (entry names → object keys, size limits, zip bombs, path normalisation).
23. `git` process execution in `SiteGitService.LocalMerge.cs` (argument construction, branch names, hooks/config in cloned repos).
24. The AI agent: browser-side tool loop with write capability (`features/agent/modes.ts`, `assistant/tools.ts`), prompt-injection classifier (`agent/classify.ts`, `injection.test.ts`), `risk` levels. Server-side limits on what the proxy forwards (`/api/admin/ai/messages` forwards an arbitrary Messages request body).
25. Tenant-authored HTML/CSS served on tenant domains and in admin previews; `hydrate.js`; site-host sets `X-Frame-Options DENY` and CSP only if configured.
26. SVG/image sanitisation (`SvgSanitizer`, `MediaSanitizer`, `ContentSniffer`); media served from content-api on tenant origins.

### SSRF / outbound HTTP
27. **User/tenant-supplied AI `BaseUrl`** → ai-gateway outbound calls (`AiBaseUrl.cs` validation; "local provider exemption" per memory note).
28. `MetaMediaMirror` downloads from URLs returned by Meta; `MetaGraphClient`.
29. The admin-api preview proxy `/api/admin/sites/{id}/preview/api/{**path}` → content-api (path handling, header injection).
30. The site-host API proxy and edge YARP transforms (Host preservation, `X-Forwarded-*` trust with `KnownProxies.Clear()` in four services).
31. edge `TlsAllowList` → site-host; DNS-01 Cloudflare writer; ACME issuance triggered by attacker-pointed domains (rate-limit budget).
32. platform-api → Loki/Prometheus delete APIs and log-janitor (container log truncation; `janitor.py` reads the Docker API).

### Anonymous endpoints & webhooks
33. POST `/api/internal/git/webhook` (HMAC compare method, replay, queueing builds) and POST `/api/internal/alerts` (sends email from the platform address). Both are reachable publicly on `ADMIN_HOST` because the edge routes all of `/api/**`.
34. GET `/api/admin/social/callback` (state validation, tenant establishment from state).
35. Public content-api surface: `collect` (any-origin CORS, volume), form submissions (spam, email triggering, no captcha evident), visitor register/login (brute force, rate limiter partitioning by forwarded IP), `_config` allow-list, OpenAPI documents, media by GUID, search/tag queries.
36. `GIT_HOST` exposes Forgejo `/api/internal/**` and `/api/v1/**` **without an edge policy** (`PlatformRoutes.cs` ForgejoGitPaths).
37. site-host `/internal/tls-allowed` and `/internal/tls-hostnames`: only "internal" because the edge does not route `/internal` on tenant hosts. The edge's tenant catch-all is `/{**catch-all}` with no `/internal` exclusion (grep of `Dcms.Edge` finds `/internal` only in `TlsAllowList.cs`, the caller). **Tenant-domain requests to `/internal/tls-hostnames` therefore appear to reach site-host** (enumerates all hosted domains). Not exercised in this phase.

### Raw SQL / data
38. Hand-built SQL in `ContentListQueries.cs`, `TagQueries.cs`, `ObservabilityQuery.cs`; `FromSqlRaw` claim queries; interpolated identifiers in `RlsConfigurator`, `AuditSchemaConfigurator`, `ObservabilityViewConfigurator` (`pg_roles` lookup with `'{role}'`), `PlatformRoleConfigurator`.
39. Tenant purge / site deletion / account deletion cascades (`TenantDeleter`, `SiteDeleter`, DELETE `/api/admin/me`): completeness across schemas, MinIO prefixes, Forgejo orgs, audit retention.
40. Audit chain integrity: HMAC key management (`AUDIT_CHAIN_KEY`), sealing, gap detection, retention deletes, the `AuditBulkCommandInterceptor` SQL parsing.

### Secrets / crypto
41. Vault Transit usage (`VaultTransitEncryptor`, key names per purpose); AppRole token expiry and re-auth (memory: 1 h tokens, VaultSharp caching; `VaultTokenRefresh.cs`).
42. Custom certificate upload (`CertificateUpload.cs`: PFX/PEM parsing, private key storage), ACME account key storage.
43. The Data Protection key ring shared by all services in Postgres; transit wrapping optional.
44. `ServiceTokenClient` token cache: registered as a typed HttpClient (transient), so the per-instance cache and semaphore may not be shared. Correctness/efficiency target.
45. The Forgejo password outbox (Data Protection-encrypted plaintext passwords for sync).

### Messaging / infrastructure
46. NATS `no_auth_user: app`: any client on the network gets the app account. No per-service subject permissions; anyone on the network can publish `site.publish.requested.*`, `audit.submitted`, `notify.raise`, `email.send`, and so on. Consumers trust message contents (e.g. the site-builder trusts tenant facts carried on the message).
47. Redis without auth in the base compose; the SignalR backplane is shared by three hubs (prefix separation).
48. The `docker-socket-proxy-ro` / `docker-socket-proxy` scope; `store-usage`, `alloy` host mounts; floating `:latest` images for Vault, MinIO, socket proxy, and the sandbox image.
49. The Forgejo SSH port `2222` published on all interfaces.
50. The forwarded-headers trust model (`KnownIPNetworks/KnownProxies.Clear()`) combined with any path that bypasses the edge.
51. SPA nginx configs set no CSP or security headers; the admin API has none either (only content-api/site-host call `UseDcmsSecurityHeaders`).

### Background jobs / complex logic
52. Outbox dispatch and scheduled publish claim semantics (duplicates, ordering). JetStream consumers are serial, and some are ordering-sensitive (memory note).
53. Site build lanes, build activation/rollback (`/builds/{bid}/activate`), cache invalidation races in site-host.
54. Certificate renewal/issuance scheduling and Let's Encrypt rate budget (`CertificateRenewalService`, `DomainCertificateProvisioner`, `ManagedCertificateGuard`).
55. Meta token refresh and sync workers; analytics/notification/AI/audit retention workers (delete scope).
56. The git merge/conflict resolution flow (`/git/merge`, `/git/merge/resolve`) and stale-edit rebase (commit `e63cda8`).

### Large / complex frontend flows
57. `features/ide` (`IdePage.tsx` 675 LOC, `ide/agent/useAgentSession.ts` 815), `features/site-source` (`vfs.ts` 702, `SourceControlView.tsx` 635), `features/builder` (GrapesJS integration; memory lists five silent-failure traps), `features/assistant` (`tools.ts` 541).
58. `packages/gjs-blocks` (`blocks-css.ts` 1237, `thumbnails.ts`, `preview.ts`) and `gjs-parse` (HTML/CSS sanitisation vs formatting); runtime parity with `StaticSiteAssembler`.
59. Largest backend files: `Sites/SiteEndpoints.cs` (1292 LOC), `ApiClientGen/TypeScriptClientEmitter.cs` (865; generates code shipped to tenants: identifier/string escaping), `TenancyEndpoints.cs` (621), `AccountEndpoints.cs` (566), `IdentitySeeder.cs` (534), `AiConversationEndpoints.cs` (516), `CertesAcmeIssuer.cs` (478).

### Dead code / hygiene / process
60. Possible dead endpoints: PUT `/sites/{id}/definition`, POST `/git/provision`, POST `/purge/prometheus`, `/api/admin/ai/ping`; unimplemented `IPluginEndpointBuilder.MapGet/MapPost/Group`; legacy subject `site.publish.requested`; retired `dcms-grafana` client cleanup.
61. The stray root `package-lock.json` alongside pnpm; the `src/Shared/Dcms.Shared.Data/bin\Debug` backslash directory (ignored); `tsconfig.tsbuildinfo` files under `apps/*` (present locally, not tracked); `graphify-out/` (ignored).
62. CI runs no frontend tests, lint or e2e; the `dotnet` job runs the integration project too (skipped without Docker).
63. `.env.example` drift (missing platform-api/edge/log-janitor AppRole vars).
64. Manually synchronised duplicates (§7.9): scopes, permission constants FE/BE, RLS table list, migrator context lists.
65. Security-pinned transitive packages in `Directory.Packages.props` (SSH.NET, System.Security.Cryptography.Xml); ImageSharp licensing pin (ADR 0001); prerelease `OpenTelemetry.Instrumentation.StackExchangeRedis`.

---

## 15. Audit Coverage Checklist

Legend: `[ ] Not reviewed` · `[ ] Reviewed` · `[ ] Deep review required`. Every item starts **Not reviewed**. Items the mapping phase recommends for deep review are also flagged ⚑.

### Backend services
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.AdminApi/Program.cs` (pipeline, prod guards, migrate-only)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Tenancy/*` (membership middleware, service principal guard, propagated actor, console caller, permission resolver, tenancy/tenant admin/invitations/my-account endpoints, provisioning, `TenantDeleter`, migrator)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Tenancy/{Domain,DomainCertificate,ManagedCertificate}Endpoints.cs`, `CertificateUpload.cs`, `DnsTxtLookup.cs`
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Sites/*` (SiteEndpoints, SiteDeletion, SitePreviewEndpoints, StaticBundleBuilder, SiteHub, SiteLiveUpdates)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Sites/Git/*` (ForgejoClient, SiteGitService + LocalMerge, RepoAccessReconciler, webhook)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Ai/*` (messages proxy, credentials, conversations, generation, prompt builder, retention)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/ApiClientGen/*` (TypeScript emitter, package builder, templates, scaffold endpoints)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Social/*` (OAuth callback, token refresh, feed sync, media mirror, stories)
- [ ] Not reviewed — `Dcms.AdminApi/Cms/*` (content endpoints, list endpoints, outbox dispatcher, scheduled publish)
- [ ] Not reviewed — `Dcms.AdminApi/Media/*` (upload endpoint, ingest service, extensions)
- [ ] Not reviewed — `Dcms.AdminApi/Plugins/*` (instances, config validator, marketplace, navigation)
- [ ] Not reviewed — `Dcms.AdminApi/Forms/FormSubmissionEndpoints.cs`
- [ ] Not reviewed — `Dcms.AdminApi/Analytics/*` (consumer, dashboard, prune, retention)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Audit/*` (chain writer, ingest consumer, projector, maintenance, endpoints/export)
- [ ] Not reviewed — `Dcms.AdminApi/Notifications/*` (hub, publishers, consumers, retention, invitation expiry, certificate notifications, platform notifications)
- [ ] Not reviewed — `Dcms.AdminApi/Chat/*`
- [ ] Not reviewed ⚑ Deep review required — `Dcms.AdminApi/Observability/AlertEndpoints.cs`
- [ ] Not reviewed — `Dcms.AdminApi/Openapi/OpenApiPreviewEndpoints.cs`
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.ContentApi` (Program, Delivery/*, Visitors/*, Forms/*, Chat/* incl. ChatHub + ChatBotResponder, Branding, Plugins/_config, Social/StoryDelivery, SandboxHeaderContext)
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.Identity` (Program, Endpoints/{Account, AccountApi, Authorization, PlatformUser}, Seeding, OpenIddictCertificates, Forgejo/*, Data, Domain)
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.PlatformApi` (Authz, Delegation, Purge, Observability clients, Reporting/ObservabilityQuery, Stores, Realtime)
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.Edge` (Program, Auth/*, Certificates/* incl. Dns/*, Protection/*, Routing/*, Transforms/*, EdgeHttpPlane, EdgeOptions)
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.SiteHost` (DomainResolver, ApiProxy, SiteHostEndpoints, internal TLS endpoints, invalidators)
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.SiteBuilder` (ReactAppBuilder, SitePublishConsumer, StaticSiteAssembler, SiteBuildLane, SandboxOptions, Runtime/hydrate.js, sandbox.Dockerfile)
- [ ] Not reviewed ⚑ Deep review required — `src/Services/Dcms.AiGateway` (Messages/Chat endpoints, AiProviderResolver, providers, bridge, AiQuota, usage scanner)
- [ ] Not reviewed — `src/Services/Dcms.MediaWorker` (consumers, transcoders)
- [ ] Not reviewed — `src/Services/Dcms.EmailWorker`

### Shared libraries
- [ ] Not reviewed ⚑ Deep review required — `Dcms.Shared.Security` (JwtBearer, permission policy provider/handlers, permission constants, ServiceTokenClient)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.Shared.Data/Tenancy` + `Rls` + `TenantEntity` + `Sandbox`
- [ ] Not reviewed ⚑ Deep review required — `Dcms.Shared.Data/Audit` (interceptors, chain, canonicalizer, retention, schema configurator)
- [ ] Not reviewed — `Dcms.Shared.Data/Cms` (incl. ContentListQueries, TagQueries raw SQL, PluginInstanceSlugs)
- [ ] Not reviewed — `Dcms.Shared.Data/{Media,Sites,Search,Analytics,Visitors,Chat,Forms,Social,Notifications,Ai,Edge,Platform,Observability,DataProtection}`
- [ ] Not reviewed — `Dcms.Shared.Data/Migrations/**` and `Dcms.Identity/Migrations`
- [ ] Not reviewed — `Dcms.Shared.Data/PostgresAdvisoryLock.cs`
- [ ] Not reviewed — `Dcms.Shared.Audit` (middleware, recorder, redaction, propagation)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.Shared.Vault` (config source, auth, token refresh, transit)
- [ ] Not reviewed — `Dcms.Shared.Messaging` (NATS publisher, audit sink, email queue, SMTP sender)
- [ ] Not reviewed — `Dcms.Shared.Hosting` (service defaults, ProblemDetails, rate limiting, security headers, health)
- [ ] Not reviewed ⚑ Deep review required — `Dcms.Shared.Media` (ContentSniffer, MediaSanitizer, SvgSanitizer, WebpLadderGenerator)
- [ ] Not reviewed — `Dcms.Shared.Storage`
- [ ] Not reviewed — `Dcms.Shared.Caching`
- [ ] Not reviewed — `Dcms.Shared.Telemetry` (incl. SensitiveAttributeProcessor)
- [ ] Not reviewed — `Dcms.Shared.Contracts` (events, streams/subjects)
- [ ] Not reviewed — `Dcms.Shared.Kernel`

### Plugin SDK & plugins
- [ ] Not reviewed — `src/PluginSdk/Dcms.PluginSdk.Abstractions`
- [ ] Not reviewed — `src/PluginSdk/Dcms.PluginSdk.Runtime` (registry, route table, OpenApiAssembler)
- [ ] Not reviewed — `src/Plugins/Dcms.Plugins.All`
- [ ] Not reviewed — `Dcms.Plugins.Forms` (config schema, public config keys)
- [ ] Not reviewed — `Dcms.Plugins.Branding`
- [ ] Not reviewed — `Dcms.Plugins.LiveChat`
- [ ] Not reviewed — `Dcms.Plugins.VisitorAuth`
- [ ] Not reviewed — `Dcms.Plugins.Meta.Core`, `Dcms.Plugins.Instagram`, `Dcms.Plugins.Facebook`
- [ ] Not reviewed — `Dcms.Plugins.{Analytics, Articles, AudioLibrary, Blog, Carousel, Events, FileDownloads, ImageGallery, Roster, Search, VideoGallery, VideoStreaming}`

### Frontend applications
- [ ] Not reviewed ⚑ Deep review required — `apps/admin/src/{main.tsx, auth.ts, useAuth.ts, tenants.ts, runtime-config.ts, routes.tsx}`, `app/*` (route guards, shell, tenant switcher)
- [ ] Not reviewed ⚑ Deep review required — `apps/admin/src/features/ide/**` (PreviewPane iframe, preview bundler worker, agent)
- [ ] Not reviewed ⚑ Deep review required — `apps/admin/src/features/agent/**`
- [ ] Not reviewed ⚑ Deep review required — `apps/admin/src/features/assistant/**`
- [ ] Not reviewed — `apps/admin/src/features/builder/**` (GrapesJS)
- [ ] Not reviewed — `apps/admin/src/features/site-source/**` (VFS, git, live updates)
- [ ] Not reviewed — `apps/admin/src/features/sites/**` (SitesPage, SiteWorkspace, StaticSitePage upload)
- [ ] Not reviewed — `apps/admin/src/features/{content, media, forms, plugins, marketplace}`
- [ ] Not reviewed — `apps/admin/src/features/{members, roles, rbac, domains, workspace, settings, invitations, account, tenants}`
- [ ] Not reviewed — `apps/admin/src/features/{ai, analytics, audit, chat, notifications, openapi, dashboard}`
- [ ] Not reviewed — `apps/admin/src/{components, chat, lib, locales}`
- [ ] Not reviewed — `apps/admin/{Dockerfile, docker-entrypoint.sh, nginx.conf, vite.config.ts, vite-plugin-palette-types.ts}`
- [ ] Not reviewed — `apps/platform/src/**` (auth, api clients, routes, features: overview, tenants, users, audit, monitoring, storage, access, certificates, notifications, live)
- [ ] Not reviewed — `apps/platform/{Dockerfile, docker-entrypoint.sh, nginx.conf}`

### Frontend packages
- [ ] Not reviewed ⚑ Deep review required — `packages/core` (auth, http client, runtime config)
- [ ] Not reviewed — `packages/ui` (permissions, live, shell, errors)
- [ ] Not reviewed ⚑ Deep review required — `packages/api-client` (shipped to tenant sites; embedded in admin-api)
- [ ] Not reviewed — `packages/site-components` (chat widget)
- [ ] Not reviewed — `packages/site-template-react` (templates, shared runtime, committed generated fixture)
- [ ] Not reviewed — `packages/gjs-schema`
- [ ] Not reviewed — `packages/gjs-parse`
- [ ] Not reviewed — `packages/gjs-blocks`
- [ ] Not reviewed — `packages/site-builder-toolchain`

### Tests
- [ ] Not reviewed — `tests/Dcms.UnitTests`
- [ ] Not reviewed — `tests/Dcms.PluginSdk.Tests`
- [ ] Not reviewed ⚑ Deep review required — `tests/Dcms.IntegrationTests` (fixtures, TestAuthHandler, tenancy isolation, RLS, permission coverage)
- [ ] Not reviewed — `e2e/**` (Playwright specs + mocked fixtures)
- [ ] Not reviewed — frontend Vitest suites (`apps/*`, `packages/*`)
- [ ] Not reviewed — `loadtest/**`
- [ ] Not reviewed — `scripts/ai-bench`, `benchmarks/ai-agent.json`

### Infrastructure, build & deploy
- [ ] Not reviewed ⚑ Deep review required — `docker-compose.yml`
- [ ] Not reviewed ⚑ Deep review required — `docker-compose.prod.yml` (docker-socket-proxy, sandbox settings)
- [ ] Not reviewed — `docker-compose.vps.yml`
- [ ] Not reviewed — `docker-compose.override.yml`
- [ ] Not reviewed — Service Dockerfiles (`src/Services/*/Dockerfile`, `sandbox.Dockerfile`), `.dockerignore`
- [ ] Not reviewed ⚑ Deep review required — `.gitlab-ci.yml`
- [ ] Not reviewed ⚑ Deep review required — `scripts/deploy.sh`, `scripts/ci/deploy-remote.sh`, `scripts/ci/resolve-digests.sh`
- [ ] Not reviewed — `scripts/obs-*.sh`, `scripts/smoke.ps1`
- [ ] Not reviewed ⚑ Deep review required — `infra/postgres/**` (bootstrap, schemas, RLS role, service/observability/platform/edge roles)
- [ ] Not reviewed ⚑ Deep review required — `infra/nats/**` (nats.conf auth, stream provisioning)
- [ ] Not reviewed — `infra/minio/init.sh`
- [ ] Not reviewed ⚑ Deep review required — `infra/vault/**` (apply.sh, init.sh, provision-host.sh, services.sh, policies, seal, server config, bundled `bin/vault`)
- [ ] Not reviewed — `infra/observability/**` (alloy, prometheus + rules, loki, tempo, grafana ini/provisioning/dashboards)
- [ ] Not reviewed ⚑ Deep review required — `infra/log-janitor/**`
- [ ] Not reviewed — `infra/acme-test/**`
- [ ] Not reviewed — `.env.example`
- [ ] Not reviewed — `Directory.Build.props`, `Directory.Packages.props` (dependency versions and pins), `global.json`
- [ ] Not reviewed — `package.json`, `pnpm-workspace.yaml`, `pnpm-lock.yaml`, stray `package-lock.json`
- [ ] Not reviewed — `eslint.config.js`, `.prettierrc.json`, `.editorconfig`, `.gitattributes`, `.gitignore`, `playwright.config.ts`

### Documentation / decision records
- [ ] Not reviewed — `docs/adr/0001–0013`
- [ ] Not reviewed — `docs/{setup, runbook, vault-secrets, seal-vault, deploy-linux, migrate-vps1-to-pipeline, plugins, mode-a-builder, ai-agent}.md`
- [ ] Not reviewed — `README.md`, `CLAUDE.md`, `AI AGENT PROGRESS.md`, `TODO/*.md`

---

### Verification note

This map was checked against the discovered inventory:
- all 48 `.csproj` projects (10 services, 12 shared libs, 2 SDK libs, 20 plugin projects, 3 test projects)
- both SPAs and all 9 `packages/*`
- all 4 compose files and 38 compose services
- the CI pipeline, deploy scripts, and all `infra/*` subdirectories
- `e2e/`, `loadtest/`, `docs/`, `TODO/`

Each appears in §2, §4, §6, §11, §12 and §15.

**Not verified in this phase** (marked `UNKNOWN` or "verify" above):
- in-handler authorization inside bare `RequireAuthorization()` endpoints
- the exact certificate private-key wrapping
- `FromSqlRaw` claim semantics
- how `site-components` is distributed
- which content-api caches listen to `plugin.instance.changed`
- a live request confirming that site-host `/internal/*` is reachable through tenant domains (the routing code suggests it is)
