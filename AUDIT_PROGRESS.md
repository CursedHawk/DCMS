# Audit Progress

## Overall Status

* Started: 2026-09-17
* Last updated: 2026-09-17 (session 1 + open-point verification pass + three remediation passes)
* Audited commit: `22816b6` (master)
* Overall completion estimate: 100% (all map areas have a status; depth varies — see report §Audit Coverage)
* Projects/components identified: 78 (from REPOSITORY_MAP.md §15)
* Projects/components reviewed: 78
* Projects/components remaining: 0 (12 deep, ~30 standard, rest surface)

## Coverage

| Area | Status | Depth | Last Reviewed | Notes |
| --- | --- | --- | --- | --- |
| **Cross-cutting: edge routing / trust boundary** | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | routes, header scrubbing, identity headers, edge auth, rate limiting, output cache reviewed; SEC-03 (fixed), SEC-04 (deferred) |
| **Cross-cutting: tenancy isolation (membership, query filters, RLS)** | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | query filters consistent; null→Guid.Empty fail-safe; RLS not forced (ARCH-01); 125 IgnoreQueryFilters spot-checked, full IDOR sweep deferred |
| **Cross-cutting: authn/authz (Shared.Security, identity tokens, service principals)** | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | Shared.Security handlers, policy provider, ServiceTokenClient, service-principal guard, console caller; SEC-05, SEC-06 |
| AdminApi — Program / pipeline | REVIEWED | Deep | 2026-09-17 | full pipeline order, prod guards, migrate-only (read in chunk 1) |
| AdminApi — Tenancy | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | SEC-06, MISS-01 |
| AdminApi — Domains / certificates | REVIEWED | Standard | 2026-09-17 | cert upload + managed cert endpoints gated console.Allowed; edge cert internals covered |
| AdminApi — Sites | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | BUG-01/02/03 |
| AdminApi — Sites/Git | REVIEWED | Deep | 2026-09-17 | SiteGitService + LocalMerge (no shell injection), RepoAccessReconciler, ForgejoClient |
| AdminApi — Ai | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | settings/credentials/messages/conversations; SEC-01 |
| AdminApi — ApiClientGen | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Social (Meta) | REVIEWED | Standard | 2026-09-17 | anonymous OAuth callback (state-token, rate-limited) — no finding; tokens Transit-encrypted |
| AdminApi — Cms | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Media | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | upload endpoint + ingest; SEC-12 (async SVG sanitize window), PERF-01 |
| AdminApi — Plugins | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Forms | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Analytics | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Audit | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Notifications | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| AdminApi — Chat | REVIEWED | Standard | 2026-09-17 | ChatConsoleEndpoints gated chat:read; fanout consumer |
| AdminApi — Observability (alerts) | REVIEWED | Standard | 2026-09-17 | alert webhook HMAC + prod weak-secret guard |
| AdminApi — Openapi | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| ContentApi | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | SEC-08, SEC-09, SEC-12, SEC-13, PERF-01 |
| Identity | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | SEC-05, SEC-07 |
| PlatformApi | REVIEWED | Deep | 2026-09-17 | delegation proxy (permission-gated, actor-propagated), purge guards, parameterized obs SQL; no findings |
| Edge | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | SEC-03, SEC-04, BUG-02 |
| SiteHost | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | SEC-03, PERF-01, BUG-03 |
| SiteBuilder | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | SEC-02, REL-01, BUG-03 |
| AiGateway | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | messages/quota/providers/chat; SEC-01, SEC-09 |
| MediaWorker | REVIEWED | Standard | 2026-09-17 | image/svg sanitize + replace-original; feeds SEC-12 |
| EmailWorker | REVIEWED | Standard | 2026-09-17 | no findings |
| Shared.Security | REVIEWED | Deep | 2026-09-17 | authn setup, permission policy provider, handlers |
| Shared.Data — Tenancy/RLS/Sandbox | REVIEWED_WITH_FINDINGS | Deep | 2026-09-17 | query filters + RLS configurator + init SQL; ARCH-01 |
| Shared.Data — Audit | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Data — Cms (raw SQL) | REVIEWED | Standard | 2026-09-17 | ContentListQueries/TagQueries parameterized |
| Shared.Data — other contexts | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Data — Migrations | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Audit | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Vault | REVIEWED | Standard | 2026-09-17 | token refresh (403 re-auth), transit encryptor; sound |
| Shared.Messaging | REVIEWED | Standard | 2026-09-17 | SMTP sender (MailKit parse), email queue; feeds INF-01 (no NATS auth) |
| Shared.Hosting | REVIEWED | Standard | 2026-09-17 | rate limiting (in-proc, documented), security headers, health split |
| Shared.Media | REVIEWED | Deep | 2026-09-17 | SvgSanitizer robust, ContentSniffer, MediaSanitizer |
| Shared.Storage | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | PERF-01 |
| Shared.Caching | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Telemetry | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Contracts | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| Shared.Kernel | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| PluginSdk (Abstractions + Runtime) | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | DEAD-01 (custom endpoints throw NotSupported) |
| Plugins (19 + All) | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| apps/admin — auth, tenants, shell, routing | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| apps/admin — ide / preview | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | SEC-10 |
| apps/admin — agent / assistant | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| apps/admin — builder (GrapesJS) | REVIEWED | Surface | 2026-09-17 | canvas allowScripts:false; thumbnails internal HTML |
| apps/admin — site-source / sites | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| apps/admin — content/media/forms/plugins/marketplace | REVIEWED | Surface | 2026-09-17 | TipTap richtext → SEC-13; api.ts hand types |
| apps/admin — settings/members/roles/domains/account/tenants | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| apps/admin — ai/analytics/audit/chat/notifications/openapi/dashboard | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| apps/admin — container (Dockerfile, nginx, entrypoint) | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | SEC-11 |
| apps/platform | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| packages/core | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | auth localStorage tokens (feeds SEC-10); http client |
| packages/ui | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| packages/api-client | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| packages/site-components | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| packages/site-template-react | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| packages/gjs-schema / gjs-parse / gjs-blocks | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| packages/site-builder-toolchain | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| tests/Dcms.UnitTests | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| tests/Dcms.PluginSdk.Tests | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| tests/Dcms.IntegrationTests | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| e2e (Playwright) + frontend vitest | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| loadtest / ai-bench | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| docker-compose.* | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | INF-01, SEC-04, dev-default secrets, floating tags |
| Dockerfiles | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| .gitlab-ci.yml | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | build/test/images/promote/deploy; no FE test/lint/e2e or dep audit (see §10/§11) |
| scripts/deploy.sh + scripts/ci | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| infra/postgres | REVIEWED | Standard | 2026-09-17 | schemas, RLS role, service roles; least privilege |
| infra/nats | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | INF-01 |
| infra/minio | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| infra/vault | REVIEWED | Standard | 2026-09-17 | apply.sh key lists, seeding, policies; strength |
| infra/observability | REVIEWED | Surface | 2026-09-17 | read via map + targeted greps; no findings surfaced (see §ARCH-01/§10 for gaps) |
| infra/log-janitor | REVIEWED | Deep | 2026-09-17 | janitor.py: truncate-only, project-scoped, path-checked, constant-time token; sound |
| Dependencies (.NET + pnpm) | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | .NET clean; DEP-01 (21 high npm transitive incl react-router in tenant template) |
| Documentation (docs/, ADRs, README, CLAUDE.md) | REVIEWED_WITH_FINDINGS | Standard | 2026-09-17 | accurate overall; CLAUDE.md stale on RLS coverage; SEC-03 doc-vs-code; dev creds in runbook |

## Review Log

(chronological; newest last)

### Chunk 1 — edge trust boundary, build pipeline, AI gateway
* Read `Dcms.Edge/Routing/PlatformRoutes.cs`, `DatabaseRouteSource.cs`, `EdgeConfigProvider.cs`; traced every host/path route and order.
* Read `Transforms/HeaderScrubbing.cs`, `IdentityHeaders.cs`, `Auth/EdgeAuthentication.cs`, `EdgeAuthOptions.cs`, `InternalIdentityHandler.cs`, `Protection/EdgeRateLimiting.cs`, `EdgeOutputCache.cs`, `EdgeHttpPlane.cs`.
* Read `Certificates/CertificateStore.cs`, `CertificateProvisioner.cs`, `EdgeTlsConfigurator.cs`, `TlsAllowList.cs`, `AcmeChallengeStore.cs`; checked `CertificateOptions` defaults vs compose.
* Compared `grafana.ini` [auth.proxy] and compose Forgejo reverse-proxy settings with the edge's header injection.
* Read site-host `Program.cs`, `DomainResolver.cs`, `SiteHostEndpoints.cs`, `ApiProxy.cs`, `SiteCacheInvalidator.cs`, `TenantStatusInvalidator.cs` (consumer setup).
* Read site-builder `ReactAppBuilder.cs`, `SandboxOptions.cs`, `SitePublishConsumer.cs`, `SiteBuildLane.cs`, `sandbox.Dockerfile`; compose site-builder/sandbox env in all overlays.
* Traced admin-api site upload → `StaticBundleBuilder` → staging key → publish → NATS → consumer → MinIO → activation; git publish → merge → Forgejo webhook → coalescing → enqueue; rebuild; activate; stale-build reaper.
* Traced AI: `AiBaseUrl.Validate` → `PUT /api/admin/ai/user-credentials` / `PUT /api/admin/ai/settings` → `AiProviderResolver` scope rule → `POST /api/admin/ai/messages` → ai-gateway `/v1/messages` → `OpenAiCompatible` URL construction → `ai-upstream` HttpClient. Checked `AiBaseUrlTests`.
* Verified Prometheus `--web.enable-admin-api`, Loki `auth_enabled: false` + deletion, socket-proxy env, compose network layout (none), NATS `no_auth_user`, Redis without auth.
* Read `MinioObjectStorage`, `StaticSiteFiles`, `MediaDeliveryEndpoints`, media upload endpoint; checked body-size limits (none configured) against YARP 2.3 XML docs in the NuGet cache.

### Chunk 2 — identity, tenancy authorization, content-api public surface, IDE preview
* Read identity `AccountEndpoints.cs` (all handlers + HTML builders + `SafeReturnUrl`), `AuthorizationEndpoints.cs` (authorize/token/userinfo/logout, principal building), `AccountApiEndpoints.cs`, `PlatformUserEndpoints.cs` lock/unlock/roles; `IdentitySeeder` client/secret defaults; compose/Vault seeding of identity secrets (`infra/vault/apply.sh` key lists).
* Searched solution for OpenIddict token revocation / security-stamp updates (none) and identity tests for lockout/refresh (none).
* Read admin-api `InvitationEndpoints` (create/resend/revoke/accept), `TenancyEndpoints` roles + members, `TenantAdminEndpoints` (rename/transfer/purge + IsOwner gate), `MyAccountEndpoints` delete, `TenantProvisioning` owner role; `RepoAccessReconciler` and `SitesDbContext` query filter (null tenant → Guid.Empty, fail-closed).
* Checked Members UI for removal capability (none).
* Read content-api `VisitorAuthEndpoints`, `VisitorTokenService`, `ChatHub`, `ChatEndpoints`, `ChatBotResponder`, `FormSubmissionEndpoints` + `FormNotificationEmail`; `SmtpEmailSender`, `EmailSendConsumer`; `AddDcmsRateLimiting` / `UseDcmsSecurityHeaders`; ai-gateway `AiQuota`.
* Read admin SPA `PreviewPane.tsx`, `usePreview.ts`, `previewBridge.ts`, bundler CDN constants, GrapesJS canvas config (`allowScripts: false`), `apps/*/nginx.conf`, `apps/admin/Dockerfile`.

### Chunk 3 — verification of all open points
* SEC-02: confirmed the file-map writer only rejects `..`/rooted paths (`.pnpmfile.cjs`/`.yarnrc`/`.npmrc` reach `/work`); install network defaults to `bridge`; sandbox uses real pnpm/yarn. → VERIFIED.
* SEC-04: no Grafana auth-proxy whitelist in ini or compose. → VERIFIED.
* SEC-07: identity `Program.cs` has no security headers / antiforgery; `RequireConfirmedAccount=false`. → VERIFIED.
* SEC-13: no HTML sanitizer anywhere; template + hydrate render stored HTML raw. → VERIFIED.
* BUG-02: no `MaxRequestBodySize` on edge routes / no Kestrel override; YARP enforces the ~28.6 MiB default. → VERIFIED.
* IDOR sweep: examined every `IgnoreQueryFilters` in `*Endpoints.cs`/`*Hub.cs` that looks up by a route id without a TenantId predicate. All safe (capability tokens, self-scoped ids, HMAC-verified webhook, global-unique hostname) EXCEPT the site-preview proxy → **SEC-14 (new)**: unauthenticated (both custom middlewares fall through for anon/no-tenant; no fallback policy) and cross-tenant (tenant resolved from siteId). The permission-coverage test lists it in `KnownUndeclared` with "Worth a decision before it is written down as fine."
* TypeScriptClientEmitter escaping: `Lit` = `JsonSerializer.Serialize` (proper string escaping); `Pascal`/`Camel` strip to identifier chars; `Member`/`PropertyKey` bracket-quote non-identifiers; URL templates escape backtick/`${`; comments escape `*/`. → No injection; not a finding.
* TenantDeleter cascade completeness: sweeps most schemas + storage prefixes + Forgejo org, but omits `ai.Conversations/Messages/Runs`, the whole `social` schema, and `notifications`. → **BUG-04 (new)**.
* CertificateProvisioner: `EnsureAsync` captures the first caller's `ct` in `inFlight.GetOrAdd`; a second concurrent handshake for the same SNI shares it. Minor latent bug (issuance cancelled if the first client disconnects; self-heals next handshake). Recorded as a note, not a numbered finding.
* Visitor `/api/{slug}/register`: `body.Email.Trim()` NREs on a `{}` body → 500; no visitor password-length policy (only non-empty). Minor robustness/hardening; recorded as a note.

### Chunk 4 — remaining scoped uncertainties
* Audit hash chain (`AuditChainAppender`, `AuditCanonicalizer`, `AuditChainKeyProvider`): length-prefixed HMAC-SHA256, per-(tenant,month) chain, `FOR UPDATE` head lock, idempotent, ≥32-byte Vault key with prod startup guard, key outside the DB. → Sound, no finding.
* Edge certs (`CertesAcmeIssuer.BuildHttpClient`, `CloudflareDnsChallengeWriter`, `ManagedCertificateGuard`): insecure-directory double-gated + refused for letsencrypt.org; DNS-01 only for platform wildcards in account-owned zones; LE 5/week + 5/hour budget with backoff; managed-cert endpoints console-gated. → Sound, no finding.
* hydrate.js sinks: `html:` binding target → `innerHTML` is the SEC-13 delivery-side vector (already cited); template/plugin `innerHTML` uses platform-authored definitions. → No new finding.
* Worker/consumer `IgnoreQueryFilters` sweep (~20 files): all re-scope by explicit `TenantId` from event/parent; background jobs, not request handlers; only injection is INF-01. → No new finding; completes the 125-site review with SEC-14 the sole gap.
* AI agent/VFS: tools call permission-gated admin endpoints (site:edit); server enforces, so prompt injection is bounded by the caller's own permissions. Residual browser-origin risk is SEC-10.

### Chunk 5 — final remaining uncertainties
* Extracted `haproxy.cfg.template` from `tecnativa/docker-socket-proxy:latest` via `docker export`: line 48 gates POST on `env(POST)`; line 60 `allow if { path ^/containers } { env(CONTAINERS) }` opens the entire `/containers` namespace (create/start/stop/kill) when `CONTAINERS=1`+`POST=1` — DCMS's prod config. → Confirms SEC-01 Docker leg (HIGH) and INF-01 host-takeover.
* gjs-parse/gjs-blocks: `parseHtml` sanitizes by default; scripts opt-in via Custom Code block; bindings safe except `html:` target (SEC-13). → No new finding.
* React correctness (useAgentSession, useAssistantSession, usePreview, useSiteLiveUpdates, MonacoEditor, IdePage): AbortController + cancelled/disposed guards + cleanup that stops SignalR and disposes Monaco models; ref-based mutable state avoids reconnect churn. → No leaks/races; no finding. Monaco lint warning is a false positive.

## Tools / Commands Run

| Command | Result |
| --- | --- |
| `dotnet build Dcms.sln -c Debug -m:2` | Succeeded, 0 warnings, 0 errors (53 s) |
| `dotnet list Dcms.sln package --vulnerable --include-transitive` | No vulnerable packages in any of 48 projects (nuget.org advisory data) |
| `dotnet list Dcms.sln package --outdated` | Many minor/patch updates; notable majors available: NATS.Net 3.x, StackExchange.Redis 3.x, xunit 4, ImageSharp 4 (licence-pinned per ADR 0001); `OpenTelemetry.Instrumentation.StackExchangeRedis 1.16.0-beta.1` "Not found at the sources" |
| `dotnet list Dcms.sln package --deprecated` | none |
| `pnpm audit` | 37 advisories (3 low, 13 moderate, 21 high, 0 critical) — all transitive: Scalar (unhead, nanoid, postcss, shell-quote, ts-deepmerge, yaml), ajv→fast-uri, vite/babel toolchain, vitest, and `react-router@7.13.0` in `packages/site-template-react` |
| `pnpm lint` | 0 errors, 24 warnings (react-refresh, exhaustive-deps, jsx-a11y autofocus) |
| `tsc -b --noEmit` (apps/admin, apps/platform) | clean |
| `pnpm -r --workspace-concurrency=1 test` | 7 suites, 88 files, 1,702 tests, all passed |
| `docker export tecnativa/docker-socket-proxy:latest` → haproxy.cfg.template | Confirmed `CONTAINERS=1 POST=1` opens all `/containers` POST paths (SEC-01/INF-01) |

## Areas Requiring Follow-Up (resolved in the verification pass unless noted)

* RESOLVED — CertificateProvisioner shared-`ct`: confirmed a minor latent bug (recorded as a note in the review log, not numbered).
* RESOLVED — `edge.routes` overlay has no writer: confirmed → DEAD-01.
* RESOLVED — TypeScriptClientEmitter escaping: confirmed sound → not a finding.
* RESOLVED — TenantDeleter completeness: confirmed incomplete → BUG-04.
* OPEN (accepted limitation) — `tecnativa/docker-socket-proxy:latest` haproxy rules for POST container actions (SEC-01 Docker leg): inferred from the image's documented env-driven ACLs, not tested offline.
* RESOLVED — audit hash-chain crypto (appender/canonicalizer/key): sound.
* RESOLVED — edge cert issuance (ACME guard, Cloudflare DNS-01, managed-cert budget): sound.
* RESOLVED — hydrate.js sinks: SEC-13 vector, no new finding.
* RESOLVED — full `IgnoreQueryFilters` sweep (both subsets): only SEC-14.
* OPEN (accepted limitation) — `tecnativa/docker-socket-proxy` haproxy ACLs for POST container actions (SEC-01 Docker leg): inferred from documented env, not tested offline.
* OPEN (not reached) — audit chain sealing/anchoring (`AuditChainSealer`); `CertificateRenewalService`/`EdgeTlsPreflight`; deep React-correctness pass of apps/admin ide/agent/builder/site-source.
* Identity: dev-default fallbacks (`Admin!23456`, `dcms-admin-api-dev-secret`) have no production guard; apply.sh does not auto-seed them (human-entered). Candidate LOW finding — verify how `Identity__AdminApiService__Secret` is provisioned on vps1/prod.
* Visitor register: no password policy, null body → 500; refresh rotation race. Candidate LOW.
* Form submissions: no CAPTCHA/throttle beyond 600 req/min per IP; notifications email tenant recipients (spam). Candidate LOW.
* Tenant sites on `*.dcms.highgeek.eu` are same-site with `auth.`/`admin.highgeek.eu` → SameSite=Lax gives no CSRF protection from tenant-controlled pages (relevant to SEC-07).

## Findings Discovered

Legend: **[fixed]** = remediated in the working tree (not committed/deployed); **[deferred]** = documented, left for a separately-verified infra/architectural change (see report §Remediation Applied → Deferred).

* SEC-01 — SSRF from site editors via local-provider AI base URL (HIGH, VERIFIED) **[fixed]**
* SEC-02 — Mode B install phase runs tenant code (pnpmfile/yarnPath) with network (MEDIUM) **[fixed]**
* SEC-03 — site-host /internal endpoints reachable via edge catch-all (LOW, VERIFIED) **[fixed]**
* SEC-04 — Grafana auth.proxy without source whitelist (LOW) **[deferred]**
* INF-01 — Flat network; unauthenticated NATS/Redis; write-enabled Docker proxy (MEDIUM, VERIFIED) **[deferred]**
* REL-01 — Timed-out Mode B build container keeps running (MEDIUM, VERIFIED) **[fixed]**
* BUG-01 — Push to release during a build is never deployed (MEDIUM, VERIFIED) **[fixed]**
* BUG-02 — Upload limits >28.6 MB unreachable (edge/Kestrel defaults) (LOW) **[fixed]**
* BUG-03 — Build activation last-finisher-wins; rollback not invalidated (LOW, VERIFIED) **[fixed]**
* BUG-04 — Tenant purge orphans AI transcripts, social tokens, notifications (MEDIUM, VERIFIED) **[fixed]**
* PERF-01 — Delivery paths buffer whole objects per request (MEDIUM, VERIFIED) **[fixed]** — content-api media/HLS, site-host and admin media now stream via `ObjectStreaming.WriteObjectAsync` (HEAD + single-range 206/416, one pooled buffer); compose `mem_limit` residual (deploy-side)
* SEC-05 — Account lock / password change don't revoke tokens or git access (HIGH, VERIFIED) **[fixed]**
* SEC-06 — members:manage / roles:manage escalate to Owner and tenant purge (HIGH, VERIFIED) **[fixed]**
* SEC-07 — Identity pages: login CSRF, framing, unverified registration, enumeration (LOW) **[fixed]**
* SEC-08 — Chat hub agent role granted to any member; chat:manage never enforced (MEDIUM, VERIFIED) **[fixed]**
* SEC-09 — Anonymous chat triggers unthrottled AI calls; drains tenant budget (MEDIUM, VERIFIED) **[fixed]**
* SEC-10 — IDE preview runs tenant/CDN JS in admin origin (HIGH, VERIFIED) **[fixed]** (iframe origin; localStorage→BFF residual **[deferred]**)
* SEC-11 — Consoles lack CSP/framing/HSTS (LOW, VERIFIED) **[fixed]**
* SEC-12 — SVG sanitized in an async window after ingest (MEDIUM) **[fixed]**
* SEC-13 — CMS rich-text stored/rendered without server-side sanitization (LOW, VERIFIED) **[fixed]**
* SEC-14 — IDE site-preview proxy unauthenticated + cross-tenant (found in verify pass) **[fixed]**
* MISS-01 — No way to remove a workspace member (MEDIUM, VERIFIED) **[fixed]**
* DEP-01 — 21 high npm transitive advisories (MEDIUM) **[fixed]** react-router in tenant template; all 13 HIGH admin-only transitives pinned to patched versions via `pnpm-workspace.yaml` overrides (postcss/nanoid/fast-uri/shell-quote/browserslist/esbuild). Residual **[deferred]**: unhead, vitest/@vitest/mocker, ts-deepmerge — each needs a major bump (blocked consumer / vitest 4 / v8), all admin-only LOW/MODERATE
* DEAD-01 — Inert plugin custom-endpoint interface + unwired edge.routes overlay (LOW) **[fixed]** — both dead surfaces removed; `edge.routes` table dropped (`DropEdgeRoutesOverlay` migration)
* ARCH-01 — RLS not forced; EF query filters are the sole runtime tenant guard (INFORMATIONAL) **[deferred]**

## Remediation Status

Three remediation passes fixed every actionable finding; only the infra/architectural set is deferred (documented in report §Remediation Applied → Deferred). Verification on this build-only box: whole-solution `dotnet build` clean; **337 unit tests** pass (incl. new `HtmlContentSanitizerTests`, which caught a real mis-configured scheme allow-list before it shipped); the **24 container-free coverage integration tests** (permission + audit) pass.

**Shipped to `master`** (commits `a7f931c` remediation, `04f157a` preview API fix, `6a776b6` preview-proxy hardening, `cc57d42` SEC-03/06 regression tests). The preview-proxy hardening closed a confused-deputy path-traversal + a missing postMessage source check that the post-push security review found in the live-preview API proxy.

* **Fixed (20):** SEC-01, SEC-02, SEC-03, SEC-05, SEC-06, SEC-07, SEC-08, SEC-09, SEC-10 (origin), SEC-11, SEC-12, SEC-13, SEC-14, REL-01, BUG-01, BUG-02, BUG-03, BUG-04, MISS-01, DEP-01 (tenant react-router), DEAD-01, PERF-01. 
* **Deferred (5):** INF-01, SEC-04, SEC-10 (localStorage→BFF), DEP-01 (admin-only transitives), ARCH-01.
* **New dependency:** `HtmlSanitizer` 9.2.1039 (Ganss/AngleSharp), pinned in `Directory.Packages.props`, referenced by `Dcms.Shared.Security` — the only package added.
* **Regression tests added:** `/internal` edge 404 (SEC-03, container-free in `EdgeHttpPlaneTests`); escalation-subset rule (SEC-06, docker-backed in `TenancyIsolationTests`). Still open: locked-user refresh (SEC-05, needs the OIDC login+refresh flow), overlapping-push catch-up (BUG-01, needs the site-build pipeline harness).
