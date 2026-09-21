# ADR 0014: The admin console authenticates by session cookie at the edge, not by a token in `localStorage`

**Status:** accepted (2026-09-21) — **complete**. All five phases landed; the soak between 4
and 5 found and fixed one bug (see phase 4). The admin console holds no credential, and no
browser client can obtain one for admin-api. Closes the SEC-10 residual from
`AUDIT_REPORT.md`. Builds on [ADR 0010](0010-yarp-edge.md).

## Context

The admin SPA holds its own OIDC tokens. `packages/core/src/auth.ts` builds an
`oidc-client-ts` `UserManager` with `userStore: new WebStorageStateStore({ store:
window.localStorage })`, authorization code + PKCE against the public client
`dcms-admin-spa`, scopes `openid profile email roles dcms.admin offline_access`.
`apps/admin/src/tenants.ts` reads the access token back out on every call and sets
`Authorization: Bearer …`. Tokens live 10 minutes; the refresh token beside them lives
14 days (`Dcms.Identity/Program.cs:119-120`).

So any script that runs in the admin origin can read a credential good for two weeks.
That is the finding. Two of its three legs are already closed:

- **SEC-10 (origin)** — the IDE preview no longer executes tenant and CDN JavaScript in
  the admin origin. The iframe is sandboxed without `allow-same-origin`, and the parent
  replays its API calls.
- **SEC-11** — the consoles now ship a CSP.
- **Residual** — everything else that could ever execute in that origin still reads the
  token. A dependency in the admin bundle, a `dangerouslySetInnerHTML` regression, an
  XSS in a page that renders tenant-authored text.

The residual has stayed open because moving tokens out of JavaScript is not a patch. It
changes how ten services see an operator, and — the part that is specific to this
platform rather than generic BFF advice — it makes CSRF a live concern where it was not
one before.

### What the layout already gives us

| Fact | Where | Why it matters here |
| --- | --- | --- |
| `adminApiBase` is `/api`, `contentApiBase` is `''` | `apps/admin/src/runtime-config.ts` | The console is **same-origin** with its API. A BFF needs no new host. |
| `admin.<domain>/api` → admin-api, `/hub` → content-api, `/*` → admin-spa | `PlatformRoutes.cs:127-138` | Two route prefixes to change, both on one host. |
| The edge is already a **confidential** OIDC client with cookie auth | `EdgeAuthentication.cs`, `IdentitySeeder.cs:266-283` | The hard half — client secret, PKCE, key ring, callback paths, open-redirect guard — exists and is in production for Grafana and Forgejo. |
| Data-protection keys live in the edge's own schema under its own application name | `EdgeAuthentication.cs` | The cookie is already forgery-resistant to everything but the key ring itself. See **Prerequisite**. |
| content-api's `Auth:Audience` is `dcms-admin-api` | `Dcms.ContentApi/appsettings.json` | **One** access token satisfies admin-api *and* the chat hub. The edge needs exactly one resource scope. |
| admin-api registers no CORS at all | `AddCors` appears only in identity and content-api | A cross-origin `fetch` to admin-api already fails preflight. That is load-bearing below. |

## Decision

**The edge becomes the BFF.** It keeps the session, holds the tokens, and attaches the
bearer on the way through. Nothing behind the edge changes: admin-api and content-api
keep validating a JWT exactly as they do now.

### 1. The edge holds the tokens

- `Edge__Auth` requests `dcms.admin` and `offline_access` in addition to
  `openid profile email roles`, and `IdentitySeeder` grants the `dcms-edge` client
  `Permissions.Prefixes.Scope + "dcms.admin"` and `Permissions.GrantTypes.RefreshToken`
  (it already has the latter).
- Tokens go into a **server-side store**, not the cookie, backed by the Redis the edge
  already depends on (its `/health` already folds in a Redis check). The principal
  carries a `dcms_sid` claim naming the row; the cookie carries nothing else new.

  Not `SaveTokens = true` into the cookie, for three reasons: an ID token plus an
  access token plus a refresh token overflows 4 KB and chunks; every refresh rewrites
  the cookie, so two concurrent requests can race and one loses the rotated refresh
  token, which kills the session; and there is no way to revoke a session that is
  entirely in the client's hands.

  **As built this is a token store keyed by a session id, not an `ITicketStore`**
  (`BffSessionStore`). It has the same three properties with less machinery: the ticket
  stays claims-only and small, refresh writes one Redis key under a lock rather than
  going through `SignInAsync` — which, with a session store, removes and re-stores the
  ticket under a *new* key on every call — and deleting the row is revocation.
- **Refresh is serialised per session.** OpenIddict rotates refresh tokens, so two
  concurrent requests that both find the access token expired will both redeem it and
  the loser gets `invalid_grant` — ending a session that was healthy. A Redis `SET NX`
  lock keyed by session id, with a short TTL and a bounded wait, is the fix.
  *ponytail: a lock per session and a re-read after acquiring it; if edge replicas ever
  make this hot, the upgrade is a single-flight per session id in-process in front of
  the Redis lock.*
- Losing the Redis session store signs everyone out. It does not lock them out:
  identity's own cookie is still in the browser, so the next request re-runs the OIDC
  redirect and returns with no prompt.

### 2. The edge injects the bearer

A new route metadata key, `dcms.bff`, set on exactly two routes — `admin-api` (`/api`
on the admin host) and `admin-hub` (`/hub` on the admin host). On a route carrying it:

1. The route requires `EdgePolicies.SignedIn`.
2. A request transform removes any inbound `Authorization` header and sets its own from
   the session's access token, refreshing first if it is within ~60 s of expiry.

Removing the inbound header matters. `HeaderScrubbing` deliberately does **not** strip
`Authorization` — Forgejo's git-over-HTTP needs it, and today the admin SPA sends it.
Once a BFF route is the one deciding who the caller is, a client-supplied
`Authorization` on that route is either noise or an attempt to use a token the session
does not own, and the fallback described in **Rollout** is the only reason it is not
removed from day one.

SignalR needs nothing special: the negotiate POST and the WebSocket upgrade are both
HTTP requests the edge proxies, so both get the header. The `access_token` query-string
plumbing in `Dcms.ContentApi/Program.cs:109-127` and `Dcms.AdminApi/Program.cs:65` can
stay (harmless) and be deleted in phase 4.

### 3. CSRF — the part that is not generic advice

A cookie is ambient. The bearer token was not. This is the one place where the standard
BFF recipe does not transfer, and it is worth being explicit about why.

**`SameSite=Lax` is not a control on this platform.** SameSite is decided per *site*, by
registrable domain. Tenant sites on `*.dcms.highgeek.eu` and the console on
`admin.highgeek.eu` share `highgeek.eu`, so a request from a tenant-controlled page to
the console is **same-site** and a Lax cookie rides along on any method. The edge's own
cookie comment already records the sibling of this fact for framing
(`EdgeAuthOptions.cs:51-60`). Tenant *custom* domains are cross-site and are covered by
Lax; the managed subdomains are not, and those are the ones a tenant can put arbitrary
HTML on.

**What already helps.** admin-api has no CORS policy, and `createApiClient` puts
`X-Dcms-Request-Id` on every request — a non-simple header, which forces a preflight
that admin-api will not answer. So every JSON call is already unreachable cross-origin.

**What does not.** `multipart/form-data` is a CORS-simple content type. `POST
/api/admin/media` from a tenant page would be *sent* with the cookie; the attacker
cannot read the response, but the write happens. That is a real vector and it is enough
to require a real control.

The decision is two controls, because the failure is expensive and one of them is a
single `if`:

- **(a) Origin allow-list at the edge**, on every unsafe method on a `dcms.bff` route.
  Accept only an `Origin` exactly equal to `https://<AdminHost>`; reject a missing
  `Origin` on an unsafe method (browsers always send it on POST/PUT/PATCH/DELETE).
  Prefer `Sec-Fetch-Site: same-origin` when present. This alone defeats the multipart
  case, because the tenant page's origin is not the admin host.
- **(b) Double-submit CSRF token.** The edge sets a readable, host-scoped, `Secure`,
  `SameSite=Lax` `dcms.csrf` cookie whose value is `HMAC(session id)` under a key from
  the edge's own data-protection purpose; the SPA echoes it in `X-Dcms-Csrf`; the edge
  compares. Binding it to the session id is what stops a token minted for one session
  being replayed into another. Being a custom header, it also forces the preflight that
  (a) would catch anyway — defence in depth, cheap.

**The structural fix, recommended separately.** Serve tenant sites from a different
registrable domain (e.g. `*.dcms-sites.example`) so they are cross-site with the console
and Lax means what it says. That also retires the hazard `EdgeAuthOptions` documents
about a domain-scoped cookie reaching site-host. It is a DNS, certificate and
published-URL change, so it is named here as the durable answer and **not** a blocker
for this ADR.

### 4. The SPA stops holding a credential

| File | Change |
| --- | --- |
| `packages/core/src/auth.ts` | `createAuth` loses `oidc-client-ts`. `login()` → `location.assign('/.edge/signin?returnUrl=…')`; `logout()` → `/.edge/signout`; `getUser()` → `GET /.edge/me` (401 = signed out); `getAccessToken()` is **deleted**. No `renewSilently` — renewal is server-side. |
| `packages/core/src/http.ts` | `headers()` no longer returns `Authorization`. Add `X-Dcms-Csrf`. A 401 means the session is gone: redirect to sign-in instead of calling `renew`. |
| `apps/admin/src/tenants.ts` | `adminHeaders()` keeps `X-Dcms-Tenant`, drops the token read. |
| `useNotificationHub.ts`, `useSiteLiveUpdates.ts`, `ChatPage.tsx` | Drop `accessTokenFactory`. Same-origin cookie rides the handshake. |
| `apps/admin/src/features/account/accountApi.ts` | ✅ Repointed to `/account/api` on the admin host in BFF mode, behind a **new edge route** → identity. Same prefix rather than `/api/account`, so the proxy needs no path rewrite. Makes it same-origin, lets the BFF attach the bearer, and retires the console's dependency on identity's `Cors:AllowedOrigins`. |
| `PreviewPane.tsx` | Unchanged in shape. It already replays through the parent; the parent now sends a cookie instead of a header, and the iframe still holds nothing. |
| `uploadWithProgress` (XHR path) | Add the CSRF header. No `withCredentials` needed — same-origin. |

`/.edge/me` is new: the claims the shell already renders (email, display name, roles),
read from the cookie session. It replaces the ID-token profile the SPA reads today.

### 5. What deliberately does not change

- **`X-Dcms-Tenant` stays a client header.** It names a tenant; it does not claim one.
  `TenantMembershipMiddleware` still resolves and authorises it. Moving tenant selection
  into the session would make a tenant switch a server round trip for no security gain.
- **The public plane is untouched.** Visitor auth, form submissions, the analytics
  beacon and site delivery have no console session and gain none.
- **The platform console is out of scope.** It talks to platform-api on its own host
  with `dcms.platform`. The same treatment applies to it later; doing both at once
  doubles the blast radius of a rollout that already has to be staged.

## Prerequisite

`EdgeAuthentication.cs` records a known gap: the edge's data-protection key ring is
**not** Vault-Transit-wrapped, so someone who can read `edge.data_protection_keys` can
forge a session cookie. Today that buys a Grafana and Forgejo session. After this ADR it
buys an authenticated admin-API session for any user.

The severity of that gap therefore changes, and wrapping the ring should land **before**
phase 3. The machinery exists (`Dcms.Shared.Data.DataProtection.TransitXmlEncryptor`);
what is missing is a configurable key name so the edge gets its own Transit key rather
than the platform-wide one that also protects every user's git credential.

## Rollout

A push to `master` is the deploy, so this cannot be one commit. Four phases, each one
independently shippable and reversible:

1. **Edge-side, code only, flag off.** ✅ *Landed.* Session store, token acquisition and
   refresh lock, `/.edge/me`, Origin check, CSRF mint/verify, `dcms.bff` route metadata,
   and the `dcms-edge` client's `dcms.admin` scope permission converging onto existing
   databases. `Edge:Auth:Bff` ships **false**.
2. **Turn the flag on.** ✅ *Landed.* `Edge:Auth:Bff` now defaults to `true`, so the
   deploy carries it; `EDGE_BFF=false` in a host's `.env` remains the kill switch. Still
   inert for the console: the middleware acts only on requests that arrive with no
   `Authorization` header, and the console sends one.

   What this step actually changes is the **sign-in**, which is why that is what
   `EdgeClientAuthorizationTests` covers — the authorization request now names two more
   scopes, and OpenIddict refuses one the client does not hold with a bare `400`
   (`invalid_request`, ID2051) rather than a redirect, on a client Grafana and Forgejo
   share. A BFF session is also minted **only on the admin host**: it is the only host
   with a route that opts in, so minting one for a Grafana or Forgejo sign-in would buy
   nothing and would make those sign-ins depend on Redis being up — a new way for the
   dashboards to be unreachable at the moment somebody needs them to find out why Redis
   is down.
3. **SPA, flagged off.** ✅ *Landed.* Cookie mode behind
   `window.__DCMS_CONFIG__.authMode` (`DCMS_AUTH_MODE` / `ADMIN_AUTH_MODE`), default
   `bearer`. Both paths build and both are tested.

   `AuthClient` now covers both shapes, so the shell cannot tell which one it is in:
   `subscribe` replaces the `UserManager` events the admin console read directly, and
   `AuthSession` replaces `User` (oidc-client-ts's `User` is structurally assignable to
   it, so bearer mode needed no mapping and **no component changed**). The platform
   console keeps its `UserManager` through the narrower `BearerAuthClient` that
   `createAuth` returns, so it was not touched at all.
4. **The cutover.** ✅ *Landed.* `DCMS_AUTH_MODE` defaults to `bff`, so the next deploy
   moves vps1 onto it. Rollback is `ADMIN_AUTH_MODE=bearer` in the host's `.env` and an
   `admin-spa` restart — a runtime value in index.html, so no revert and no pipeline.

   **Order matters on the way back.** The console sending no `Authorization` header only
   works against an edge that attaches one, so `EDGE_BFF` must not be turned off while
   `ADMIN_AUTH_MODE` is still `bff` — that combination 401s every API call. Roll the
   console back first, the edge second.

   **The soak found a bug, and it is fixed.** Every hub 403'd at the edge: a WebSocket
   handshake over HTTP/2 is an extended CONNECT (RFC 8441), not a GET, so the CSRF branch
   demanded a header a handshake cannot carry. REST worked perfectly beside it, and it hit
   two services at once, which is what pointed at the edge. Fixed by asking
   `HttpContext.WebSockets.IsWebSocketRequest` rather than keying on the method — and while
   there, by *starting* to check the origin of a handshake at all: the HTTP/1.1 shape is a
   GET, so it had been passing unexamined, which is cross-site WebSocket hijacking on a
   host whose tenant subdomains are same-site. See `BffGuard.IsWebSocketHandshake`.

   The soak is a human step and deliberately not automated: real `SameSite` behaviour
   across real hosts, and the tenant-subdomain CSRF attempt, are the things this box
   cannot see. What to watch, in the order they would break: sign-in returns to where it
   started; a write succeeds (CSRF); an upload succeeds (the multipart path the Origin
   check exists for); the notification bell connects (SignalR handshake carrying the
   injected header); account settings load (the new same-origin route).
5. **Remove the fallback.** ✅ *Landed, after the phase-4 soak came back clean.*
   - `dcms-admin-spa` lost the `dcms.admin` scope. **This is the commit SEC-10 was about**:
     it turns "the console no longer puts a token in localStorage" into "a token in
     localStorage could not call admin-api if it were there", and the second survives a
     regression in the first. The client is kept, registered and scopeless — its redirect
     URIs are still the console's, and re-granting a scope is a one-line change where
     re-registering a client is not.
   - The edge now **strips** a client-supplied `Authorization` on a BFF route, and
     unconditionally — including when there is no session, which would otherwise be the one
     state in which a caller could still choose its own bearer.
   - `oidc-client-ts` is gone from the admin console, along with `authMode`, the OIDC
     runtime config, the `/auth/callback` route and the `DCMS_OIDC_AUTHORITY` the container
     was given. `@dcms/core` keeps `createAuth` for the platform console, which has not
     moved.
   - The `access_token` query-string handling is gone from both hubs, and with it a token in
     a URL — and therefore in an access log, a `Referer`, and anything that samples either.
     Site visitors are unaffected: the chat widget has never sent a token at all.

   **The rollback is now a revert.** `ADMIN_AUTH_MODE` no longer exists; `EDGE_BFF=false`
   alone would leave the console sending no credential to an edge attaching none.

**Why the flag exists, and why it is two steps rather than one.** Turning the BFF on makes
the edge request `dcms.admin` and `offline_access` at sign-in, and OpenIddict refuses an
authorization request naming a scope the client does not hold. The edge and identity ship
in the same push, but the edge does not wait on identity in `depends_on` — so a rolling
deploy can start the edge first, and the symptom of losing that race is every operator
locked out of Grafana. Shipping the code inert and flipping it afterwards removes the
ordering question, and leaves a kill switch on the public ingress that needs no revert.

`EdgeAuthOptions.Enabled` is false when no client secret is configured, and that is a
deliberate open loop: the edge must not refuse to start and take every tenant site
offline over a console setting. The BFF must respect it — with auth disabled, the
`dcms.bff` routes carry no policy and the SPA stays in bearer mode. Phase 4 therefore
makes the client secret mandatory for the admin console specifically, which is a
deployment note rather than a code change.

## How it is verified

- **Unit** — ✅ `BffGuardTests` (CSRF bound to its own session, junk and foreign-key-ring
  tokens refused rather than thrown, the Origin decision as a table including the
  same-registrable-domain tenant page and the CORS-simple multipart forge, and
  `ShouldAuthenticate` — one case per way phase 1 stays inert); `BffTokenTests` (refresh
  rotation kept vs. taken, a sign-in with no refresh token refused, an unusable
  `expires_in` not marking the token expired on arrival).
- **Seeder** — ✅ `IdentityClientSeedConvergenceTests` boots identity twice over one
  database and asserts the edge client *gains* `dcms.admin` without losing what it had.
  Mutation-checked: it fails with the convergence reverted to URIs-only. The same
  convergence is what makes phase 5's removal apply to databases that already exist.
- **Sign-in** — ✅ `EdgeClientAuthorizationTests` drives the real authorization endpoint
  with the scopes the flag adds and asserts it reaches the login page, with the scopes it
  asked for before as a second case and a scope the client does not hold as the control.
  Mutation-checked: removing the `dcms.admin` grant fails it. Phase 5 added the mirror
  image: `dcms-admin-spa` is refused `dcms.admin` outright, while still being able to sign
  somebody in.
- **Integration** (`EdgeHttpPlaneTests` already drives the edge in-process) — no cookie →
  challenge, not a proxied 200; cookie → `Authorization` present downstream; inbound
  `Authorization` on a BFF route → replaced, not forwarded; cross-origin multipart POST →
  refused; missing `X-Dcms-Csrf` → refused; concurrent expired-token requests → one
  refresh, one surviving session (this is the one that catches the rotation race, and it
  should fail with the lock removed).
- **SPA** — ✅ `packages/core/src/auth.test.ts`: `getAccessToken()` returns undefined (the
  absence the whole mode rests on — a string there and the edge passes the request through
  untouched, silently un-doing the cutover); a 401 from `/.edge/me` is signed-out rather
  than an error; a session with no `sub` is no session; a network failure keeps the session
  instead of signing the operator out of a page they are working in; subscribers fire on
  change and not on a repeat read; sign-in/sign-up/sign-out navigate to the edge carrying
  the return path; and `csrfHeader` refuses a cookie that matches only because `.` is a
  regex wildcard.
- **e2e** — ✅ `pnpm e2e`, 81 tests, and since phase 5 the admin fixtures *are* the BFF: the
  shared `test` fixture serves the console through `useBffMode` rather than seeding an OIDC
  user, because there is no bearer mode left to seed one for. The bearer sign-in spec was
  deleted and its register case moved across. `admin/bffAuth.spec.ts` drives the real console in BFF
  mode against a stand-in for the edge (`fixtures/bff.ts` substitutes `__DCMS_AUTH_MODE__`
  into the served document exactly as the container entrypoint does) and asserts what only
  a browser can say: **no `Authorization` header on any API call**, the CSRF token echoed
  on a write and absent when the edge minted none, the workspace header unchanged, the
  sign-in screen on a 401 with no requests issued, and sign-in handed to the edge carrying
  only a return path. Mutation-checked: making `getAccessToken()` return a string fails the
  no-Authorization assertion — which is the failure that would otherwise be silent, because
  the edge leaves a request with its own bearer alone and the cutover would look like it
  happened while nothing had.
- **Only on vps1** — real `SameSite` behaviour across real hosts, the tenant-subdomain
  CSRF attempt against a real browser, and cookie size after chunking. None of that is
  observable on this box, which is why phase 3 exists as its own step.

## Risks

- **content-api's default CORS policy is `AllowCredentials()` with an explicit origin
  list** (`Dcms.ContentApi/Program.cs:144-150`; prod sets
  `Cors__AllowedOrigins__0: ${PUBLIC_BASE_URL}`). Harmless while the credential is a
  bearer token the SPA attaches deliberately; under cookie auth it becomes a credentialed
  cross-origin surface. Audit the list before phase 3, and consider dropping the default
  policy entirely — the chat hub is reached same-origin through the edge, so the admin
  SPA no longer needs it.
- **One session, wider blast radius.** The edge cookie currently grants Grafana and
  Forgejo; afterwards it grants the admin API too. The host-scoped cookie keeps it off
  the other hosts, and the ticket store makes revocation real — but the Prerequisite is
  what keeps this from being a downgrade.
- **Sign-out must clear the ticket store**, not just the cookie. A cookie-only sign-out
  leaves a live session id that a captured cookie can still present.

## Alternatives considered

- **Tokens in the cookie (`SaveTokens = true`).** Simplest to write. Rejected on size,
  the refresh-rotation race, and having no revocation.
- **BFF inside admin-api.** Would need its own cookie stack, would not cover the chat hub
  in content-api, and would put a browser session inside a service that today only ever
  sees a validated JWT.
- **In-memory access token + HttpOnly refresh cookie.** Genuinely better than
  `localStorage` and much smaller. Rejected because it still hands JavaScript a live
  credential, and it needs the same CSRF work on the refresh endpoint — most of the cost
  for a fraction of the benefit.
- **`sessionStorage` instead of `localStorage`.** Equally script-readable. Buys a shorter
  window and costs a sign-in per tab. Not worth a release.
- **Leave it.** Defensible: the specific path that made SEC-10 a HIGH is closed, and
  what remains is the risk every token-in-JS SPA carries. Rejected because this console
  administers every tenant's content, media, git repositories and AI credentials, and a
  single XSS anywhere in it currently yields 14 days of that.

## Consequences

- No credential in browser storage; an XSS in the console gets the user's *session* for
  as long as the page is open, not a portable 14-day refresh token.
- Ten services keep validating a JWT. The BFF is confined to one file's worth of edge
  transform plus a session store.
- The console gains a hard dependency on edge auth being configured — an operational
  change, recorded in the Rollout section.
- CSRF becomes a standing concern for the admin host and must be considered for every new
  unsafe endpoint. That is the price of an ambient credential and it is paid in exchange
  for it not being readable.
