# ADR 0022: User Authentication: tenant-scoped enterprise users, signed in by Identity, enforced at the edge

**Status:** accepted (2026-10-07) · being implemented (kanban epic #219)

## Context

Tenants want to put their hosted sites, Dynamic Apps and other plugin backends behind real
enterprise sign-in. That means:
- the company's Google Workspace or Microsoft Entra ID, any OIDC provider, or invite-only
  passwords;
- groups and roles deciding who may open which page, read which table, run which flow, or call
  which API.

What exists falls short in three ways:
- **VisitorAuth** gives public visitors accounts, but it gates content only in the browser: a
  gjs block shows or hides a section, and nothing on the server protects a page.
- **The site plane carries no permissions.** It refuses every contract operation that asks for
  one (`ContractDispatcher`), so there is no role-based access for anyone outside the console.
- **The platform Identity service** (OpenIddict) signs in admin members, with one platform-wide
  Google client. No tenant can bring its own provider, and an admin account is global across
  tenants.

## Decision

### Accounts belong to a tenant: Identity realms

Enterprise users sign in through the existing Identity service, in a **realm per tenant**:
- **Separate pool.** A realm user (`RealmUser`) is not a `DcmsUser`: separate tables, separate
  store, separate cookie scheme.
- **Strictly tenant-scoped.** An account on tenant A does not exist on tenant B until one is
  created there.
- **Admin accounts stay separate.** A person who administers a tenant and also uses its sites
  has two accounts.
- **"Sign in with DCMS"** is one of a realm's providers. It links the person's platform login
  to their own account in that realm, and never lets a platform session satisfy a realm.
- **Why Identity.** It already has the hardened parts: password hashing and lockout, reset
  email, sessions with per-device revocation, and Vault. It is also the one service allowed to
  hold the tenant's provider secrets. content-api, the public plane, must never be able to
  decrypt a tenant credential.

How it is built (UA1):
- **The model.** `realms`, `realm_users`, `realm_groups`, `realm_group_members` and
  `realm_logins` live in Identity's schema. They are not ASP.NET Identity stores, so nothing is
  shared with `DcmsUser`'s tables.
- **The cookie.** One cookie scheme, but a cookie per realm (`dcms.realm.{tenantId}`), named
  from the realm the request is for. Every read re-checks status, lockout and the security
  stamp.
- **The pages.** `/realm/{slug}/login|forgot|reset|invite|logout`, on Identity's own host.
  There is no sign-up: accounts come by invitation, or from a trusted provider in UA2.
  Invitation and reset links are time-limited, single-use (bound to the stamp), and take their
  origin from the configured issuer, never from the request's Host.
- **The client.** Each realm's client `site:{tenantId}` is confidential and needs PKCE. Its
  secret is HMAC-SHA256 of the client id under one master secret (`Identity:EdgeSites:Secret`)
  that only Identity and the edge hold, so the edge needs no per-tenant secret store.
- **The admin API.** `/api/realms/{tenantId}/…` answers only admin-api's service token with
  scope `dcms.realms`.

Realm tokens carry:
- `sub`: the realm user id;
- `realm`: the tenant id;
- `aud = dcms.realm:{tenantId}`;
- email, name and groups.

Every consumer checks that the audience is the request's own tenant.

### Providers are tenant configuration

Each realm enables any of these:
- Google Workspace, optionally restricted to a hosted domain;
- Microsoft Entra ID;
- generic OIDC, by issuer;
- invite-only email + password;
- DCMS.

**Provisioning policy.** Invite-only, or just-in-time for allowed email domains, with default
groups and a mapping from IdP group claims. Accounts link by verified email only.

**Secrets.** Client secrets are written to Vault by Identity and never returned to anyone.

How providers work (UA2):
- **One scheme per provider.** Each provider is one ASP.NET `OpenIdConnectHandler` scheme
  (`realm-oidc-{providerId}`), added while running. The platform's vetted handler does state,
  nonce, PKCE and token validation for every tenant's client.
  - Its callback `/realm/sso/{providerId}/callback` is what the tenant registers at the
    provider.
  - A callback reaching a process that has not built that scheme yet (after a restart, or on
    another replica) re-registers it first.
- **Accounts link to a stable subject**, not an email:

  | Provider | Subject |
  | --- | --- |
  | Google | `sub` |
  | Entra | `tid:oid` |
  | OIDC | `{issuer}|sub` |
  | DCMS | the platform user id |

- **When an email counts.** An email may find, link or create an account only when the provider
  vouches for it:
  - Google: `email_verified`, plus `hd` when a Workspace domain is set.
  - Entra: only with the `xms_edov` claim (the email's domain is verified by that directory).
    Entra's `email` is an editable attribute, so it is otherwise not evidence ("nOAuth"). The
    configuration must also name one directory; `common`, `organizations` and `consumers` are
    refused.
  - OIDC: `email_verified`.
  - DCMS: a confirmed platform email, or an invitation link.
- **No self-service sign-up.** An invitation link links whichever provider account accepts it.
  Otherwise only the allowed-domains policy creates accounts: default groups, plus a mapping
  from a provider group claim to realm groups that is kept in step at every sign-in.
- **"Sign in with DCMS"** reads the platform cookie only to learn which platform account is
  being linked. Its completion must carry a value from a cookie set when this browser started
  the round trip, so a planted link links nothing.
- **Secrets.** Client secrets are encrypted under the Transit key `dcms-realm-secrets`, both
  directions Identity's alone, and are write-only through the admin API.
- **No reaching inside.** A tenant names its issuer, so every provider back-channel connection
  (discovery, keys, token, redirects included) goes through `PublicEgress`, which refuses
  private, loopback, link-local and CGNAT addresses at connect time. Issuers must also be public
  DNS names: no IP literals, no bare service names.
- **Passwords can be switched off** per realm: the form and its endpoints go together.

### Pages are gated at the edge

The edge already signs people in at the boundary and keeps their sessions.
- **Rules.** For tenant hosts it applies ordered **path rules per site**: `public`, `signedIn`,
  or `groups: […]`. The first matching prefix wins.
- **Sign-in.** Without a session, the edge challenges the tenant's realm through Identity, using
  one OIDC client per tenant whose redirect URIs are the tenant's verified domains. A signed-in
  user outside the allowed groups gets a 403.
- **Forwarding.** With a session, the edge forwards the realm access token on every proxied
  request for that host. So a public page's API calls know the user too.

How it is built (UA3):

- **Where the rules live.** admin-api's `SiteGatePublisher` writes one `edge.site_gates` row per
  verified hostname bound to a site of a tenant with the plugin enabled: the realm slug and that
  site's rules in order. A site without rules still gets a row, so its pages can offer sign-in.
  Gate edits publish at once; a reconciler every 5 minutes catches domains, plugin switches and
  deleted tenants. The table is host-keyed and outside tenant RLS, like the rest of `edge`.
- **How the edge learns.** It holds every row in memory. It re-reads the table on
  `edge.site-gates.changed` (TENANCY stream) and every 2 minutes regardless. A failed reload
  keeps the rules it has, and unreadable rules gate the whole host behind sign-in: both fail
  closed.
- **Matching.** On the path as Kestrel decoded it, never decoded again, with dot segments and
  repeated slashes resolved; case-insensitively, by whole segment (`/portal` covers
  `/portal/x`, not `/portals`). The first rule wins; `public` returns the path to public. On a
  gated host a path still holding `%` (an encoded slash, a double encoding) or `\` gets a 400:
  the edge and the server behind could read it as different files. A top-level `a_b.html` is
  judged as the page `/a/b` too, because that is the flat file site-host serves that page from.
- **Until the rules are first read** (the database unreachable at startup) every tenant host
  answers 503, retried every 5 seconds: no rules known must not mean no rules.
- **Sessions.** A separate cookie scheme (`dcms.site`, host-only) and OIDC scheme from the
  platform's, under `/.edge/site/…`, so a platform session never satisfies a site and the two
  callbacks never collide. Tokens stay server-side in Redis; the cookie holds only a session
  id. Refresh rotates the tokens and re-reads groups; a refused refresh ends the session.
- **The client secret** of `site:{tenantId}` is HMAC-SHA256 of the client id under a key only
  Identity and the edge hold (`EDGE_SITES_SECRET`, generated by the deploy). There is no
  per-tenant secret to store, rotate or leak, and a token is accepted only if its audience is
  the host's tenant client and its `realm` claim is that tenant.
- **Answers.** No session: a page load is sent to sign in, anything else gets 401. Signed in but
  outside the rule's groups: 403. Site sign-in not configured on this edge: 503 for gated paths,
  never the page.
- **Forwarding** is `X-Dcms-Realm-Token`, stripped from every inbound request like the other
  identity headers. Gated responses are never served from or stored in the output cache, and
  leave as `Cache-Control: private, no-store` whatever site-host said.
- **Endpoints on every gated host:** `/.edge/site/signin?returnUrl=`, `/.edge/site/signout`, and
  `/.edge/site/me` (the signed-in user as JSON, for the site runtime).

### The plugin is the management and policy layer

`user-auth` is a single-instance plugin.
- **Admin screens:** users, groups, roles, providers, site access rules and sessions. User, group
  and provider changes go through Identity's realm admin API, called by admin-api with a service
  principal.
- **Policy store.** The plugin's own schema `userauth` holds what Identity does not need at
  sign-in time:
  - **roles:** named bundles of permission keys;
  - **grants:** a role given to a group or a user;
  - **gates:** the site path rules.
- **Permission keys** are `{plugin}:{instance}:{resource}:{action}`, e.g.
  `dynamic-apps:crm:table:deals:read` or `dynamic-apps:crm:flow:approve:run`. They are data, not
  manifest entries: a tenant's tables are runtime data.

Contracts (`Dcms.Plugins.UserAuth.Api`):

| Contract | Purpose |
| --- | --- |
| `users.identity@1` | The signed-in enterprise user of this request, or null. |
| `users.access@1` | Whether that user holds a permission (groups + direct grants). The policy enforcement point for every consumer. |
| `users.resources@1` | Open: each plugin lists what it can gate, for the permission picker. |
| `users.directory@1` | Users and groups for the assistant and other plugins (Admin \| Ai). |
| `visitors.identity@1` | Also provided, so "own records" in Dynamic Apps and Forms work for enterprise users unchanged. Consumers take the first provider that recognises the request. |

**Recognising the user in content-api.** A `DcmsUser` authentication scheme validates realm
tokens against Identity's keys and the request's tenant. It is never the default scheme, like
`DcmsVisitor`, and `ActorKind.EndUser` names enterprise users in the audit log.

How it is built (UA4):

- **Screens.** Three, on the instance's page: *Users* (users and groups), *Access* (each site's
  rules, and roles with who holds them) and *Sign-in* (passwords on or off, and providers).
  Sessions are not a screen of their own: "sign out everywhere" is a user action.
- **Passing calls to identity.** The console routes for users, groups and providers check the
  member's plugin permission, then pass the call to identity's realm admin API for this tenant
  (`RealmAdminClient`, client credentials, scope `dcms.realms`) and answer with identity's own
  status and message. The wire records live in `Dcms.Shared.Contracts.Realms`, shared by both
  sides. Identity unreachable, or refusing admin-api itself, is a 502.
- **The realm follows the plugin.** It is created the first time the console needs it, or when
  the publisher next runs, named after the tenant. Its hosts are exactly the hostnames the edge
  gates; disabling the plugin empties them and keeps the users. Purging the tenant deletes it.
- **Grants outlive nothing.** Deleting a user or a group at identity deletes the grants to it.
  A site rule naming a deleted group keeps the id and admits nobody through it: closed, rather
  than a rule silently widening.
- **Site rules** are replaced as a whole per site, in order. A prefix must be a plain path
  (no `%`, `\`, `//` or dot segments, which the edge would refuse anyway), unique per site
  ignoring case and a trailing slash, and a `groups` rule needs 1 to 50 groups.
- **Permissions** are the union of the roles granted to the user and to the groups in their
  token. A role or grant change applies on the user's next request; a group membership change
  applies with their next token, within minutes.
- **The user in content-api** comes from `X-Dcms-Realm-Token` only — never from
  `Authorization`, where platform and visitor tokens travel — and is accepted only if its
  audience is `dcms.realm:{request tenant}` and its `realm` claim is that tenant. An otherwise
  anonymous request becomes that user's, so audit records name them.

### Enforcement where the resources are

- **Dynamic Apps.** A table's public access gains member actions (read, create, update and
  delete), each gated by its permission. Manual flows can be made runnable from the site, under
  a permission.
- **Other plugins' site APIs** are gated by the site's path rules at the edge: `/api/{slug}` is a
  path like any other, so a rule can admit only signed-in users or some groups to a plugin's API.

How it is built (UA5):

- **Roles are the switch.** There is no per-table "members" setting: a table, or a manual flow,
  is open to whoever holds its permission, and closed to everyone else, exactly as the roles
  say. The role editor offers every published table's read, create, update and delete and every
  manual flow's run (`users.resources@1`, provided by Dynamic Apps); permissions are named by
  API name, so renaming a table renames its permissions.
- **What a permission adds.** `dynamic-apps:{slug}:table:{t}:{action}` lets its holders do that
  action through the app's site API whatever the table's public access, on every record — an
  "own records" table stops filtering to their own. Hidden fields stay hidden: members are still
  the site, not the admin. Without a permission, an enterprise user is a visitor like any other.
- **Site-started flows.** `POST /api/{slug}/flows/{flow}/run`, for holders of
  `…:flow:{flow}:run`, throttled like other site writes and with input capped at 16 KB. The run
  is a manual run with the definer's rights (ADR 0021); a flow using an action that needs a
  member permission cannot be started from the site at all, since a site user holds none.
- **Not built:** a `SitePermission` on site-exposed contract operations, and per-instance API
  route rules in content-api. No operation or plugin needs either yet; the edge's path rules
  cover the plugin APIs. They come when a plugin needs a permission finer than a path. The
  limit to know about: path rules are per site hostname, so a tenant with several sites gates
  a plugin's API on each.

### On the page (UA6)

Signing in never happens in the page: it is the edge's (`/.edge/site/signin`, `/signout`), and the
session is a cookie the page cannot read. What the page gets is `/.edge/site/me` — signed in or
not, name, email, group ids.

- **Builder blocks** (shown when the plugin is enabled): a sign-in button that returns to the page
  it was clicked on, a sign-out button, and a section shown only to signed-in users or only to
  signed-out ones, with the user's name. The section is cosmetic; the site's access rules are what
  keep a page private, and the block says so.
- **The page runtime** (`hydrate.js`) asks `/.edge/site/me` once per page and offers
  `window.dcms.user.current() / signIn(returnUrl) / signOut()` to custom code.
- **The site API client** (`@dcms/api-client`, and so the generated client and the React
  templates) gains `siteUser()`, `siteSignInUrl()` and `siteSignOutUrl()`. Same origin only: the
  cookie is the site's.
- **Not built:** a group-specific section (group ids mean nothing to an author choosing in the
  canvas; protect the path with a groups rule instead), and visual-builder (Mode D) components —
  its code reaches `siteUser()` through the client.

### Automation, events and the assistant (UA7)

- **Actions** (`automation.actions@1`): `user-auth.invite@1`, `user-auth.add-to-group@1` and
  `user-auth.remove-from-group@1`. People are named by email (what an app's records hold), groups
  by id or name. Each needs `users-manage` of whoever publishes the flow (ADR 0021). Invite is
  idempotent: someone who already has an account is left as they are, so a retried step does
  not fail on the account its first try made.
- **Events** under `users.directory@1`: `user.invited` (published by the plugin wherever an
  invitation is sent: console, contract, flow) and `user.activated` (an invitation accepted, or
  an account made on first sign-in through a trusted provider). Identity announces activations on
  `tenant.realm.user-activated` (TENANCY) after the save; admin-api's plugin relays them, durably,
  as `user.activated` in that tenant. Both are best effort at the source: the account or the
  invitation stands whether or not a flow hears of it. Dynamic Apps flows trigger on both.
- **Not built:** `user.signed-in`. Every sign-in as an event is volume with little a flow would
  do with it; the audit log already has sign-ins.
- **The assistant** reaches `users.directory@1` (list, invite, enable or disable, group
  membership; disabling is dangerous) on instances with AI tools on. Provider secrets and site
  rules are not contract operations, so no tool can read a secret or change who reaches a page.

### Acceptance (UA8)

- **The realm admin API has a client of its own.** `dcms.realms` was first granted to
  `dcms-admin-api`, whose secret content-api also holds; so the plane that serves the public
  sites could have invited, disabled or re-providered any tenant's users. It now belongs to
  `dcms-realm-admin` alone (secret `Identity__RealmAdminService__Secret` = admin-api's
  `Realms__ClientSecret`, generated together by `infra/vault/apply.sh --seed`), the policy
  checks the client as well as the scope, and the old client loses the scope on the next start.
- **Isolation is tested where it is enforced:** a realm token is nobody on another tenant
  (content-api), a platform session never satisfies a realm (identity), a tenant's rules never
  apply to another host (edge), and the realm API refuses every client but one.

### Single-page apps (2026-10-08)

The first React site behind rules showed what path rules cannot do: a rule on
`/audio-library-1/track` refused the direct load, while clicking there inside the app worked —
the navigation was no request, and the page's data came from `/api/audio-library-1/track`,
covered only by the `/` rule. Decided:

- **Data is gated where it is served.** `userauth.api_rules` restricts one plugin instance's site
  API — its own routes, content delivery, config and site contracts, i.e. every endpoint whose
  route starts `/api/{slug}` — to any signed-in user or to `{plugin}:{slug}:api:read|write`
  (contracts judged by the operation's risk, not the POST). content-api checks the endpoint
  routing matched, not the path text, so no spelling (`/API/…`, an instance id for the slug)
  reaches it unchecked. Search and tags drop restricted instances the caller cannot read; on the
  site plane a call content-api did not check (the chat hub, the bot in the background) reads
  none of them, and the admin plane hides nothing.
- **The app asks the edge, the edge answers with its own decision.** `/.edge/site/access` runs
  the gate's `Decide`; the runtimes' route guards act on it by leaving the app for a real page
  load, so the edge's sign-in and 403 page are the only UI and can never disagree.
- **Not attempted:** gating the bundle per route (it is one artifact), protecting media by URL,
  and one rule language for pages and APIs. The console states these limits where rules are
  edited, and warns when the `/assets` rule would leave people a blank page.

## Consequences

- **Identity grows a second user population.** Isolation is enforced structurally (separate
  store and cookie, audience bound to the tenant), and tested for: a tenant-A user is refused on
  B, and a platform session never satisfies a realm.
- **Sign-in happens on Identity's domain**, under `/realm/{tenant}`, branded with the tenant's
  name and logo. Sign-in on the tenant's own domain is a later step.
- **Rule changes are near-immediate.** They reach the edge with the change event; a missed
  event costs at most the 2-minute sweep, and a domain or plugin change at most the 5-minute
  reconciliation.
- **Two plugins answer "who is the visitor".** With VisitorAuth and User Authentication both
  enabled, consumers ask every `visitors.identity@1` provider and take the first that recognises
  the request's credential.
