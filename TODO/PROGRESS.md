# Working notes

Progress against `FIXES, CHANGES, AND ADDITIONS.md`. One section per backlog area.
Kept here so an interrupted session can be picked up without re-deriving anything.

---

## ✅ Members — pending invitations and their roles

**Backend** — `src/Services/Dcms.AdminApi/Tenancy/InvitationEndpoints.cs`

- `GET /api/admin/invitations` — every unaccepted invitation for the tenant,
  newest first, with parsed `roleIds`, `createdAt`, `expiresAt` and an `expired`
  flag. Expired ones stay in the list rather than disappearing: an admin asking
  "why hasn't this person joined?" needs to see the dead invite to act on it.
- `POST /api/admin/invitations/{id}/resend` — mints a *fresh* token and pushes the
  expiry out, so the old link stops working.
- `DELETE /api/admin/invitations/{id}` — revoke. Deleting the row *is* the
  revocation: acceptance looks up the token hash, so an outstanding link becomes
  invalid with no extra state.
- `POST /api/admin/invitations` now rejects an address that is already a member,
  and replaces any existing live invitation for the same address instead of
  stacking duplicates.

No migration needed — `Invitation` already carried everything required.

**Frontend** — `apps/admin/src/features/members/MembersPage.tsx`,
`features/rbac/api.ts` (`useInvitations`), locale keys in `locales/{en,cs}/common.json`.

A "Pending invitations" table sits below the members table (they are not members
yet, and resend/revoke apply to nothing else) showing the invited roles, invited
date, expiry-or-`Expired` badge, and resend/revoke actions.

## ✅ Members — invites do not send emails

- admin-api now registers `AddDcmsEmailQueue()` and enqueues a rendered
  invitation email on both create and resend, following the password-reset
  producer pattern in `Dcms.Identity/Endpoints/AccountEndpoints.cs`. Mail is
  best-effort: a queue outage is logged, not surfaced, because the invitation is
  already persisted and the raw link is still returned to the inviter.
- `DedupeKey` is the invitation's token hash. The token is regenerated on every
  send, so a double-submitted invite dialog results in exactly one email.
- admin-api gained `UseForwardedHeaders` (as identity already had). Without it
  `Request.Scheme` is `http` behind Caddy and the emailed accept link would have
  pointed at `http://`.
- `POST /api/admin/invitations/accept` now also returns `tenantSlug`/`tenantName`,
  and `InviteAcceptPage` selects that tenant on success — an invitee accepting
  their first invitation previously landed on a workspace-less shell.

---

## Notes / decisions worth keeping

- **Monaco Ctrl+F** (item removed from the list by the user mid-session): worth
  recording that the earlier `overflowWidgetsDomNode` work in
  `features/site-source/MonacoEditor.tsx` cannot have been the fix — Monaco's
  find widget is an **overlay** widget (`addOverlayWidget`, `TOP_RIGHT_CORNER`),
  hosted inside the editor's own DOM. `overflowWidgetsDomNode` only affects
  *overflow* widgets (hovers, suggest). If Ctrl+F resurfaces, look elsewhere.
- `window.confirm` is the established confirmation pattern in this SPA
  (media, forms, file tree), so ordinary destructive actions use it too. Deletes
  that are irreversible *and* destroy more than one thing use the new
  `ConfirmDeleteDialog` (type-the-name) instead.
- There are two different `Hint` components in the SPA; `ui/tooltip.tsx`'s
  `Tooltip` is the raw Radix Root, not a `content`-prop wrapper. New icon buttons
  use `title` + `aria-label` to avoid the Radix trigger-composition trap noted in
  memory.

---

## ✅ Domains — remove, show target site, primary domain

**Backend** — `src/Services/Dcms.AdminApi/Tenancy/DomainEndpoints.cs`

- `GET /api/admin/domains` now returns `siteId` **and** `siteName`. The name is
  resolved server-side on purpose: listing sites needs `site:edit`, and someone
  with only `domains:manage` must still see which site each domain serves.
- `DELETE /api/admin/domains/{id}` — removes the domain. Note site-host caches
  host→site routes for 5 minutes (`SiteHost/DomainResolver`), so serving stops
  within that window rather than instantly.
- `POST /api/admin/domains/{id}/primary` — sets the canonical hostname.
- `POST /api/admin/domains/{id}/site` moved here from `SiteEndpoints.cs` (it is a
  domain route and it is where primary bookkeeping belongs) and now accepts a
  **null** `siteId` to unlink.

**Primary is now per-site, not per-tenant.** That is what "primary domain" means
everywhere else, and it is the only reading that works once a tenant has several
sites. Bookkeeping:

- linking the first domain to a site makes it that site's primary;
- moving or unlinking a domain drops the flag (it cannot follow the domain);
- deleting/unlinking the primary promotes the alphabetically-first remaining
  *verified* domain, so the choice is stable rather than insertion-ordered;
- `IsPrimary` no longer gets set at verify or provision time — there is no site to
  be canonical for yet.

Consumers (`OpenApiPreviewEndpoints`, `ApiClientEndpoints`) only use it to order
the advertised server list, which still works.

**Frontend** — `features/domains/DomainsPage.tsx`. The site `<Select>` was
uncontrolled, which is the actual "doesn't display which site it points to" bug —
it always rendered the empty placeholder however the domain was linked. It is now
controlled by `siteId`, with an explicit "not linked" option (Radix reserves `''`,
hence the `__unlinked` sentinel). Plus "Make primary" and a remove button.

## ✅ Sites — delete a site

**New** — `src/Services/Dcms.AdminApi/Sites/SiteDeletion.cs` (`SiteDeleter` +
`DELETE /api/admin/sites/{id}`), registered in `Program.cs`.

Ordering is deliberate: **database rows first, in a transaction; external systems
afterwards, best-effort.** A storage failure then leaks bytes (recoverable by
hand); the reverse order would leave a live site row pointing at artifacts that
are already gone, which the serving path cannot recover from. The response
reports `storageFailed` so the SPA can say "deleted, but files remain".

Cleanup covers: builds, drafts, the site row, domain unlinking (hostnames stay
with the tenant), **`repo:{siteId}:read|write` role permissions** — these would
otherwise linger in roles as keys the catalog no longer lists, invisible in the UI
and impossible to remove from it — object-storage prefixes in both the sites and
build-logs buckets, and the Forgejo repo.

Required permission is `site:publish`, not `site:edit`: deleting destroys build
history and the git repo, so it takes the strongest site permission that exists
rather than the everyday editing one.

**Supporting changes**

- `IObjectStorage` gained `ListKeysAsync` + `DeletePrefixAsync` (batched at 1000,
  the S3 `DeleteObjects` limit) — a published build is hundreds of small files, so
  one round trip per key would make deleting a site take minutes. Needed again for
  tenant and account deletion.
- `ForgejoClient.DeleteRepoAsync` (404 counts as success) and
  `SiteGitService.DeleteRepoAsync` (best-effort, logs an orphaned repo).
- New `components/ConfirmDeleteDialog.tsx`: type-the-name confirmation for deletes
  that are irreversible and destroy more than one thing. `window.confirm` stays
  right for a single recoverable row; it is too thin a guard for a git repo.

## ✅ Tenant — delete and transfer ownership

**New** — `src/Services/Dcms.AdminApi/Tenancy/TenantAdminEndpoints.cs`:
`GET/PATCH/DELETE /api/admin/tenant` and `POST /api/admin/tenant/transfer`,
plus a `TenantDeleter`. Registered in `Program.cs`.

These act on the **ambient tenant** (the `X-Dcms-Tenant` header), not an id in the
path, like the rest of the tenant-scoped API — there is then no way for a path and
a header to disagree about which tenant is being destroyed.

**Ownership model.** There is no owner column: ownership is *holding the system
role named `Owner`* (`TenantProvisioning.OwnerRole`). Transfer therefore means
"grant Owner to the target, remove it from everyone else" — a real transfer, not a
co-ownership grant, so afterwards exactly one owner exists. Affected members get
their cached permissions dropped, a `membership.changed` event, and a git repo
access reconcile (the outgoing owner may have just lost push rights everywhere).

**Authorisation is two-layered.** `tenant:settings` opens the settings page, but
transfer and delete additionally require the caller to actually *hold* the Owner
role — a role could hand `tenant:settings` out freely. The platform SuperAdmin
always passes, so a tenant whose owner has left is not stuck.

**Delete** sweeps every schema. Sites go through `SiteDeleter` first (that is what
removes the git repos and per-site artifacts, and it leaves the Forgejo org empty
so it can be deleted), then a per-schema `ExecuteDelete` sweep on `TenantId`, then
media/site/build-log storage prefixes, then the Forgejo org. Each schema is swept
in its own transaction — they are separate DbContexts and cannot share one without
a distributed transaction. Not every tenant-scoped entity derives from
`TenantEntity` (`AnalyticsEvent`, `DailyRollup`, `TenantAiSettings`,
`UserAiSettings` just carry a `TenantId` column), so the sweep is written as
explicit per-set deletes rather than a generic helper.

**Deliberately not done:** no `tenant.deleted` event. Nothing consumes one, and the
two caches that could go stale both self-heal — the Redis permission cache is keyed
per tenant+user and will never be consulted again, and site-host's `DomainResolver`
expires within 5 minutes.

**Frontend** — new `features/workspace/WorkspacePage.tsx` at `/workspace`, in the
admin nav behind `tenant:settings`. Rename, owner display, transfer, and a danger
zone whose delete dialog lists the real counts (sites, content, media, members,
domains) before asking the operator to type the workspace **slug**. The slug is
shown as fixed: it is the tenant header, the Forgejo org name and part of every
stored object key.

**Observation for the lazy-loading item (#11):** `vite build` puts Monaco in its own
3.8 MB chunk, but `routes.tsx` imports `SiteWorkspace` *statically*, and that pulls
Monaco in — so it loads on every page, including the dashboard. Same story to check
for GrapesJS and esbuild-wasm (12 MB).

## ✅ Account — delete an account (incl. the "owns a tenant" question)

**The behaviour you asked to define:** you may delete your account whenever you are
not the **sole owner** of a workspace. Sole ownership is the one condition that can
strand a workspace with nobody able to administer it, so it blocks deletion, and
the account page names the workspaces and points at transfer-or-delete. Being a
plain member, or a co-owner, blocks nothing — deletion simply removes you and
leaves the workspace and its content alone.

**Deletion is split across two services on purpose:**

1. `DELETE /api/admin/me` (admin-api, `Tenancy/MyAccountEndpoints.cs`) — the
   **reversible** half. Re-checks sole ownership, then removes the user's
   memberships and role assignments across every tenant, deletes pending
   invitations addressed to them, and drops per-user leftovers in other schemas:
   IDE working drafts (`sites.site_drafts.UserId`) and their own AI provider
   settings (which hold an encrypted API key). Idempotent.
2. `DELETE /account/api/me` (identity, `Endpoints/AccountApiEndpoints.cs`) — the
   **irreversible** half. Deletes the mirrored Forgejo account (with `purge=true`,
   which Forgejo requires before it will delete an account that owns anything) and
   then the identity user, after clearing queued `ForgejoSyncOutbox` rows that
   would otherwise recreate the git account.

Identity **refuses while any tenant membership remains**, so the irreversible half
cannot run first even if called directly, and a client that dies between the two
calls leaves an intact, retryable account. Neither service writes the other's
tables: identity's only view of tenancy is a `count(*)` over
`tenancy.tenant_memberships`, done with raw SQL over its own connection (both
schemas share a database) rather than by taking `TenancyDbContext`, which would
drag Finbuckle's ambient-tenant resolution into a service that has no tenant.

Forgejo deletion is best-effort and logged: an orphaned git account is tidy-up,
whereas refusing to delete a login because a side system is down is not a
defensible answer to "delete my account".

**Frontend** — `features/account/DeleteAccountCard.tsx` (danger zone on the account
page) + `accountApi.deleteAccount()`. Shows blocking workspaces as badges when
present, otherwise how many workspaces you will leave, and requires typing your own
email address. On success it ends the session (end-session redirect, falling back
to clearing the local session) rather than leaving a signed-in shell whose every
request 401s.

**Not built:** a per-workspace "leave" action. It was written and then removed —
nothing in the backlog asks for it, and it does not unblock the sole-owner case,
which is the only thing that blocks deletion. `DELETE /api/admin/me` covers the
flow that is actually needed.

---

## ✅ Admin SPA — lazy-loading scripts (Monaco, GrapesJS, …)

**Initial download: ~7.3 MB → ~2.65 MB (2.05 MB → 0.88 MB gzipped).**

Measured from `vite build`'s `dist/index.html`, which lists exactly what the
browser fetches before first paint.

### The actual bug: Vite's preload helper landed in the Monaco chunk

`monaco` already *had* its own chunk and every importer of it was already behind
`React.lazy` — yet `index.html` carried
`<link rel="modulepreload" href="/assets/monaco-*.js">` (3.8 MB) plus
`monaco-*.css` (133 KB) **on every page, dashboard included**.

Cause: Vite's `vite/preload-helper` is a tiny synthetic module the entry chunk
always imports. `manualChunks` did not assign it, so Rollup was free to fold it
into any manual chunk — and it chose `monaco`. That single edge
(`import{_ as _e}from"./monaco-*.js"` in the entry) made the whole editor an
eager dependency. Pinning the helper to `vendor` in `vite.config.ts` fixed it.

This is worth remembering as a class of bug: **with `manualChunks`, an unassigned
shared module can be absorbed into a big named chunk and drag it into the entry
graph.** Check `dist/index.html`, not the chunk list, to know what is eager.

### Every page except the dashboard is now `React.lazy`

`routes.tsx` statically imported 16 page components, so their libraries were in
the entry graph regardless of chunking — that is how `rjsf` (200 KB, only
`PluginsPage`) was eager. A `page()` helper now lazies them all; `AppShell`
already wraps `<Outlet />` in Suspense, so no per-route fallback was needed.
`child()` takes `React.FunctionComponent` (the router's `RouteComponent` rejects
class components, and a lazy wrapper is not a plain function).

### The `vendor` catch-all stays — with the editor libraries pulled out

An intermediate attempt removed `node_modules -> 'vendor'` entirely to let Rollup
split per route. **That was worse** (~4.4 MB eager): with no catch-all, Rollup
folded shared modules — React itself — into `charts` and `scalar`, so the entry
statically imported both, and `scalar` ballooned 1.5 MB → 3.2 MB. One predictable
shared chunk is the point of the catch-all; it was reverted.

What was legitimately taken out of `vendor` is a new `editor-libs` chunk (515 KB):
`css-tree`, `htmlparser2`, `js-beautify`, `zod` — reached only through
`@dcms/gjs-parse` / `@dcms/gjs-schema`, i.e. only from the builder and IDE.

### Result

Eager: `index` 82 KB + `vendor` 2.57 MB.
Lazy: `monaco` 3.85 MB · `scalar` 1.5 MB · `grapesjs` 1.14 MB · `esbuild` 12.3 MB ·
`editor-libs` 515 KB · `IdePage` 469 KB · `charts` 310 KB · `BuilderPage` 257 KB ·
`rjsf` 200 KB · `signalr` 55 KB, plus a 5–23 KB chunk per page.

`vendor` at 2.57 MB is the remaining bulk — React, TanStack router/query, Radix,
framer-motion, i18next, oidc-client-ts, lucide. All genuinely shell-wide; further
splitting there would be a separate exercise with much smaller returns.

---

## ✅ API Docs — scroll bugs and the `./favicon.ico` storm

The two symptoms turned out to share a cause: **Scalar rewrites the document URL
as you scroll**, and the app was mis-embedding it.

### `./favicon.ico` on every scroll step

`apps/admin/index.html` had no `<link rel="icon">` at all, so the browser fell
back to requesting `/favicon.ico`. nginx's SPA fallback answered that with
`index.html` — an HTML body for an image request, which the browser never caches,
so it re-asks whenever the document URL changes. Scalar changing the URL as you
scroll turned that into a request per scroll step.

- `index.html` now carries an inline `data:image/svg+xml` icon (costs no request).
- `nginx.conf` returns **204** for `/favicon.ico` instead of letting it fall
  through to `index.html`, as a backstop for anything that still asks.

### Scrolling

Scalar is built to own a whole browser tab. Its `.references-layout` sets
`min-height: 100dvh` / `--full-height: 100dvh`, and its sidebar is
`position: sticky` with `height: calc(100dvh - var(--refs-header-height))`. In the
admin SPA it lives inside `<main>`, which is *itself* the scroll container and
sits below the 3.5rem topbar. Two concrete breakages:

1. The wrapper was `overflow-hidden`. An element with a clipping overflow becomes
   the scroll container that `position: sticky` descendants resolve against — so
   Scalar's sidebar was measured against a box that never scrolls and did not
   stick at all. It also clipped Scalar's popovers. The wrapper keeps its border
   and rounding; only the clipping is gone.
2. Every `100dvh` overshot the visible area by exactly the topbar's height, so the
   sidebar was taller than the space it had and its last entries — and its own
   scrollbar — could not be reached. A `.dcms-scalar` block in `index.css`
   re-points that maths at `calc(100dvh - 3.5rem)`. `!important` is required: the
   upstream rules are `[data-v-*]`-scoped selectors of equal specificity whose
   source order we do not control.

**Noted, not changed:** `packages/site-template-react/index.html` has no favicon
either, so hosted Mode B sites have the same 404-per-URL-change behaviour. Left
alone deliberately — that is tenant-authored output, and stamping a DCMS icon on
their sites would be wrong. Worth offering as a per-site branding setting instead.

---

## ✅ Monaco/IDE — refresh DCMS-generated content

A Mode B site is scaffolded once from the tenant's content API
(`SiteScaffoldEndpoints`, flavors `openapi` / `client` / `starter`), so its
`openapi.json` and typed client under `src/api/` go stale the moment a plugin is
installed or reconfigured. There was no way to pull the new endpoints in short of
re-scaffolding the site.

- **Backend**: new `regenerate` flavor on
  `GET /api/admin/sites/{siteId}/starter-files`. It re-runs the starter emitter but
  returns **only** `openapi.json` and `src/api/**`. Deliberately not the whole
  starter — that would overwrite `src/App.tsx`, `package.json` and `.env`.
- **Frontend**: `ideApi.regenerate()` plus a **"Refresh API"** button in the IDE
  toolbar. Files land through `writeFile`, not `seedStarter`/`importFiles`, so they
  become ordinary pending edits carried by the normal autosave + git commit, and
  the author's active tab is not stolen. Only files whose content actually differs
  are written, and the toast says how many changed (or that nothing did).

Not added for Mode A: those sites are HTML/CSS in git with no generated DCMS
client, so there is nothing to refresh.

---

## ✅ Media — lazy previews, folder tiles, upload progress

### Lazy loading of previews

Media bytes sit behind a bearer-protected endpoint, so `AuthedImage` has to
*fetch* them itself — `<img loading="lazy">` is not available. Every thumbnail
therefore fired a request on mount, so opening a library of 500 assets fired 500
requests immediately.

`MediaThumb` now gates the fetch on an `IntersectionObserver` (400px `rootMargin`,
so the image is there before a normal scroll reaches it), rendering its existing
type-icon box until then. The observer is in `MediaThumb`, not `AuthedImage`,
because `MediaThumb` already owns a definitely-sized square box — `AuthedImage`'s
other caller (the detail dialog) is `max-h-full max-w-full`, which has no box
before the image loads and would never intersect. Falls back to eager loading
where `IntersectionObserver` is absent.

The pickers (`MediaPicker`, `MediaMultiPicker`) use `MediaThumb`, so they get this
for free.

### Folders as tiles

Folders were only in the left rail. They now also appear as tiles at the head of
the asset grid, so the library browses like a file manager: "All media" shows
top-level folders, and opening a folder shows its children (honouring `parentId`,
which the data model has always supported even though the rail renders flat). The
Unfiled view has no folders by definition, and search results are a flat asset
list, so neither shows tiles.

### Upload progress bars

New `api.uploadWithProgress()` in `lib/api.ts`. This is the one place the app uses
`XMLHttpRequest` instead of `fetch` — a `fetch` request body is not observable, so
upload progress is impossible with it. It deliberately does **not** replay on 401
the way `request()` does: a replay would re-send the entire file, and the token is
read immediately before the send.

- `MediaUploader` shows a row per queued file with name, size, live percentage and
  a bar; successful rows clear when the batch finishes, failed rows stay (they are
  the only record once the toast is gone). Uploads stay **sequential** — per-file
  bars are only meaningful if the browser is not splitting bandwidth six ways.
- `StaticSitePage` (Mode C bundle upload, often tens of megabytes) shows a bar and
  switches to "processing…" at 100%, since the server is still unpacking after the
  bytes land.
- New `components/ui/progress.tsx`, exported from the `ui` barrel.

---

## ✅ Content Management

### Save and Publish

`POST /api/admin/content/{id}/publish` now accepts an optional `data` body: the
edits become a new draft version and *that* version is published, in one
transaction. The editor's new **"Save and publish"** button sends the current form
state.

This is a correctness fix as much as a convenience one. Publishing pinned whatever
was last *saved*, so an author who typed and then clicked Publish silently shipped
the previous revision. Scheduling had the identical trap, so it takes `data` too.
A brand-new item has no id to publish against, so that one path still saves first
and publishes second.

### Scheduled publishing — does it work?

The machinery was sound: `ScheduledPublishWorker` polls every 15s and claims rows
with `FOR UPDATE SKIP LOCKED`, so replicas can run it concurrently and each item
fires once. Three things around it were not.

1. **Re-scheduling stacked a second row.** Nothing removed the previous pending
   schedule, so an item re-scheduled from Friday to Monday published *twice* —
   on Friday with the old version, then again on Monday. Scheduling now deletes
   the item's pending schedules first: one per item.
2. **A queued publish was invisible.** `GET /content/{id}` now returns
   `scheduledPublishAt`, the editor shows it with a **Cancel** button, and
   `DELETE /content/{id}/schedule` cancels it. There was previously no way to
   see or stop a schedule once set.
3. **The list's "Scheduled" filter matched nothing.** `ContentStatus` is
   Draft/Published/Archived — there is no `Scheduled` — yet the status dropdown
   and `statusTone()` both offered it. The list endpoint now reports
   `scheduledPublishAt` and the SPA derives "Scheduled" from it. The server stays
   truthful (a scheduled item genuinely *is* a draft) rather than gaining a fourth
   enum value and a migration. An already-published item with a queued revision
   stays "Published" — that is what a visitor sees right now.

### Publishing flow to the queue service and worker?

**Already there, no change needed.** Publish/unpublish write a `cms.content_outbox`
row in the *same transaction* as the status change; `OutboxDispatcher` polls every
2s and relays it to NATS as `content.published` / `content.unpublished`,
at-least-once, stamping `SentAt`. Scheduled publishes go through the same outbox,
so manual and scheduled publishing are indistinguishable downstream. This is a
transactional outbox, which is the right pattern here — it cannot publish an event
for a transaction that rolled back, and it cannot lose one that committed.

### Tags — can they be linked together?

Tags are free-text `string[]` inside the version JSON, with no shared vocabulary.
Nothing stopped "Live", "live" and "live music" being entered as three tags, which
silently splits what should be one group — so in practice items were often *not*
linked even when the author meant them to be.

New `GET /api/admin/content/tags?instanceId=&contentType=&field=` returns the tag
vocabulary already in use for that field, most-used first, read from each item's
**current draft** (a tag added five minutes ago should already be offered). It is
raw ADO rather than EF: the query is a `jsonb_array_elements_text` unnest plus a
group-by, which EF cannot translate, and `Database.SqlQuery` only projects scalars.
All inputs are bound parameters.

`TagsInput` gained a suggestions dropdown (case-insensitive match, `onMouseDown`
so the input's blur doesn't tear the list down before the click lands), and
`ContentEditor` fetches one vocabulary per Tags field — a type can declare several
(Events has `genres`), so `useQueries` keeps the hook count stable.

**What this does not do:** there is still no tag *taxonomy* — no parent/child, no
synonyms or aliases. That needs a real tag entity with relations and a management
UI, which is a schema change and a product decision rather than a fix. Suggestions
deliver the linking that matters day to day; say the word if you want the full
taxonomy and I will scope it separately.

---

## ✅ Permissions — links to used features, better role editing

### Link to used features

The catalog was built from plugin **manifests**, i.e. from whatever ships in the
binary. A plugin with no enabled instance in this tenant still contributed
permissions, indistinguishable from real ones, that granted access to nothing.

`GET /api/admin/permissions/catalog` entries now carry a `feature`:

| kind | what it names | route |
| --- | --- | --- |
| `platform` | the platform itself | the admin page the key gates |
| `plugin` | the tenant's **enabled instances** of that plugin | `/plugins` |
| `site` | the site whose repository the key covers | `/sites/{id}` |

plus `inUse`, false when a plugin has no enabled instance here.

The matrix shows the instance names under each plugin permission (a bare plugin
name would not say *which* gallery), links through to the feature, and folds
"not in use" entries away behind a "show N unused" toggle — **except** ones the
role already holds, which stay visible or they could never be revoked from that
screen.

### Better role editing

- **Search** across permission name, key, group, feature and instance names.
- **Select all / clear per group**, and a live count of what is selected.
- **Member count per role** in the list (`memberCount` on `GET /admin/roles`) —
  editing a role nobody holds is free, editing one held by fifteen people is not,
  and the list now says which before the dialog opens.
- **Delete confirmation** naming the role, and how many members lose what it
  grants. Deleting previously fired immediately with no prompt at all.
- Roles are ordered by name rather than by insertion.

---

## ✅ Analytics

### Expanded scope

Events previously carried type, path, referrer, session and a props blob. They now
also carry **device / browser / OS**, **country**, and **UTM source / medium /
campaign** (migration `20260819181718_AnalyticsDimensions`).

Everything new is derived **server-side at ingest, from the request** — never from
the beacon payload. A page can claim any country it likes, and it cannot see its
own IP at all. `ContentApi/Delivery/RequestEnrichment.cs` holds the User-Agent
classifier: deliberately a small set of substring rules rather than a UA-parsing
library, because the dashboard groups by these values, so coarse stable buckets
("Chrome", not "Chrome 121.0.6167.85") are what is wanted, and it avoids a
dependency whose regex database has to be kept current. Bots are **classified, not
dropped** — "how much of this is real traffic" is a question the dashboard should
answer, and silently discarding them makes totals unexplainable.

The site runtime (`SiteBuilder/Runtime/hydrate.js`) now also reports:

- the path **including its query string** — that is where `utm_*` lives, and it was
  being cut off by `location.pathname`, so campaign attribution could never work;
- **outbound link clicks** and **file downloads** (delegated from the document, so
  links added later are covered);
- **`pageleave`** with seconds on page, via `visibilitychange` — the only signal
  that fires reliably on mobile, where `unload` often does not.

`keepalive: true` matters on the last two: the page may be tearing down.

Rollups now key on the path **with the query stripped**, so `/pricing?utm_source=x`
and `/pricing` are one row instead of one row per campaign link.

### Naming

The old locale block had a single key `"pageviews"` whose value was **"Events"** —
the headline number was labelled as one thing and computed as another. There are
now four distinct, correctly named measures: **Visitors** (distinct visitor hashes),
**Sessions**, **Page views** (`type = pageview` only) and **Events** (everything).
API field names follow: `summary.{events,pageviews,visitors,sessions}`, `series`
(was `byDay`), `topSources`, `byCountry`, `byDevice`, `byBrowser`, `byCampaign`.

### Clearing statistics

`DELETE /api/admin/analytics`, optionally `?before=<instant>` for retention. Guarded
by `tenant:settings`, not `analytics:read` — destroying history is a workspace act,
not a reporting one — and behind the type-the-word confirmation dialog.

Rollups are only deleted for days that fall **entirely** inside the window: a rollup
row is a whole day's total, so removing it for a partially-cleared day would discard
counts for events that are still there.

### Views and filtering

Filter by event type, country, device and path prefix, over 7/30/90/365 days.
`GET /api/admin/analytics/dimensions` feeds the dropdowns from what this tenant
actually has, rather than a hard-coded list. Every breakdown card is clickable and
sets the matching filter, so "mostly mobile" drills into just that slice. Countries
render through `Intl.DisplayNames` in the reader's own language.

Two sources, chosen per question: totals and the unfiltered time series come from
the cheap pre-aggregated rollups; anything needing a dimension the rollups do not
carry reads raw events. **Unique visitors can never come from a rollup** — distinct
counts do not sum, so a per-day visitor column could not be added into a period
total without over-counting anyone who returned the next day. New index on
`(TenantId, OccurredAt, VisitorHash)` for that DISTINCT.

### Geolocation — read this before expecting country data

The pipeline is complete: capture → resolve → store → aggregate → display. The
resolver is an interface, `IGeoIpResolver`, because the right answer differs per
deployment.

The shipped implementation, `HeaderGeoIpResolver`, reads a country header set by the
edge — `CF-IPCountry` by default, configurable via `Analytics:CountryHeader`. That
is safe **only** because the header comes from our own edge and content-api is not
reachable except through it.

**On vps1 today this will produce no country data.** Caddy does not stamp a country
header, so the resolver returns null and the Countries card shows its "no data"
message. To get countries you need either (a) an edge that stamps one — Cloudflare
in front of Caddy is the least work — or (b) a database-backed resolver
(MaxMind GeoLite2 / DB-IP) registered in place of `HeaderGeoIpResolver` in
content-api's `Program.cs`. I did not add (b): it needs a NuGet dependency, a
licensed `.mmdb` file and a refresh story, which is your call rather than mine.
Everything downstream is already in place, so it is a one-line registration swap
plus the resolver class.

---

## ✅ Cookies — Admin SPA and hosted sites

The two surfaces genuinely need different answers, so they got different ones.

### Admin SPA — a notice, not a gate

`app/StorageNotice.tsx`, shown once and dismissible.

Everything the admin stores is **strictly necessary** for a tool you have signed
in to use: the session tokens, the selected workspace (`dcms.tenant`), theme,
sidebar state, open editor tabs. Under ePrivacy that class of storage does not
require consent, and offering a "decline" that would break sign-in would be
dishonest. So it explains what is kept and why, and that is all.

### Hosted sites — real consent, gating real tracking

The site runtime's only storage is a per-visit id (`dcms-sid`) that the server
hashes into an anonymous visitor. That is analytics storage, not
strictly-necessary storage, so in most European readings of ePrivacy it needs
consent. Implemented end to end:

- **A site setting**, not a platform one — the obligation is the site owner's and
  the wording has to be theirs. `cookieConsent { mode, message, acceptLabel,
  declineLabel, policyUrl, policyLabel }` in `site.json`, mirrored in the zod
  schema (`packages/gjs-schema`) and the C# model, and editable in the builder's
  Settings panel.
- **`mode` defaults to `banner`.** A site nobody has configured does not silently
  track. `off` is available and carries a visible warning about needing a lawful
  basis.
- **The policy is embedded in each page** (`<script type="application/json"
  id="dcms-consent">`) rather than fetched: the runtime must know the answer
  *before* it may write a session id or fire the first beacon, and a request to
  find out would either delay every pageview or race it.
- **`send()` is the single gate.** `sessionId()` is only reached from inside it,
  so with consent absent or refused **nothing is written and nothing is sent** —
  not merely "the beacon is dropped".
- Accepting sends the pageview for the page consent was given on, so a visitor who
  says yes is counted from there rather than only from the next page.
- Answers persist in `localStorage`: being re-asked every visit is what makes
  banners hated. Storage being blocked entirely (private mode) is treated as a
  **refusal** — we cannot record an answer, so we must not assume one.
- Decline comes first in the DOM so it is not the default focus target. An accept
  button that catches a stray Enter is not consent.
- The banner styles itself from `--dcms-consent-*` variables at low specificity, so
  a site can restyle it from its own `global.css`.

**A site with analytics switched off shows no banner.** The publish path now reads
whether the tenant has the Analytics plugin enabled and stamps `analytics: true|false`
into the consent block (site-builder gained a read-only `CmsDbContext` for this one
question; if the lookup fails it assumes enabled, erring towards asking rather than
tracking). Without this, every Mode A site would have greeted visitors with a cookie
banner for storage it never performs.

Covered by two new `StaticSiteAssemblerTests`.

### ⚠️ Behaviour change on next publish

Mode A sites already published have no `cookieConsent` in their `site.json`, so
they inherit the **`banner`** default. The next time such a site is published, if
its tenant has analytics enabled, visitors will see a consent banner and analytics
will record nothing until they accept — so expect recorded traffic to drop. That is
the intended safe default; a site owner who has established they do not need consent
can set the mode to `off` in the builder's Settings panel.

---

# Round 2 — 2026-08-20

The backlog file was replaced with eight new items. All eight are done. Nothing in
this round is deployed yet.

## Monaco (Mode B) — folder move and deletion

Folders are not stored — they exist only as shared prefixes of file paths — so every
folder operation is a bulk operation over the files under one prefix.

`vfs.ts` gained `deleteFolder(folder)` and `renameFolder(from, to)`. Both keep the
existing per-file save semantics: a file the server knows about joins `deletedPaths`
(so the delta carries a delete op), a local-only one just disappears; open tabs, the
active file and open diff tabs are all repointed or dropped. `renameFolder` resolves
the whole move up front and refuses as a unit — a folder left half-moved because one
child collided is worse than one that refuses — and rejects a move into itself.

`FileTree.tsx` gained folder hover actions (move/rename via prompt, delete with a
confirm naming the file count) **and** drag-and-drop: files and folders are
draggable, folders and the tree background (= repo root) are drop targets. The
existing external-upload drop zone steps aside when the drag carries the internal
`application/x-dcms-path` type — that type is readable during `dragover` while the
payload is not, which is exactly what the handoff needs.

## Mode A — data lists

**Nested and multiple repeats.** `renderTemplate` expanded only the *first*
`[data-dcms-repeat]` in a whole template, so a second list published as a single
unbound row and a nested one never expanded at all. Both renderers now expand every
outermost repeat recursively:

- a bare `data-dcms-repeat` at the top level means "once per fetched item" (unchanged),
- `data-dcms-repeat="crew"` iterates that array **on the item in scope**, at any depth,
- inner repeats are expanded inside each clone *before* the clone is bound, or the
  inner template's single row would be filled once and copied per entry,
- a `[data-dcms-empty]` sibling of a repeat now belongs to *that* repeat rather than
  to the component, so a nested empty list says "none yet" independently of the outer one.

This is the shape a tenant component reaches for as soon as its content is not flat —
an event's crew under each event.

**Positional sources.** `#index` was the only positional source, so a template had no
way to say "the first one is the big card" or to stripe its rows (`:nth-child` in a
stylesheet cannot reach a class name or an attribute). Added `#number`, `#count`,
`#first`, `#last`, `#even`, `#odd`, `#parity` (`even`/`odd`, for a `class` binding)
and `#value` (the entry itself, inside a repeat over plain values). All are offered
in the binding panel via `META_FIELDS`.

**Canvas list fidelity.** `PreviewBridge` drew whatever `/admin/content` returned —
everything, in `updatedAt` order — while delivery serves published items in
`publishedAt` order. An author laying out "the three latest" was laying out three
items the site would never show. The canvas now filters to published and sorts the
way delivery does, falling back to unpublished rows only when nothing is published
yet (otherwise a new collection would be an empty canvas).

## Mode A — click-through to an item

Two halves, and the second is the one that made the feature real.

**The link.** `linkField` only worked for content carrying a URL of its own, which
almost none does: an event links to *its own page on this site*, built from its slug.
Added an `itemLink` prop — a pattern (`/events/{slug}`, or just `/events/` for the
same thing) expanded per item, with `{slug}`, `{id}` and `{anyField}` substituted.
A placeholder that resolves to nothing yields no link rather than an address with a
hole in it. Precedence: an explicit `linkField` mapping wins, then the pattern, then
the conventional guess; `-` on the mapping still means "not a link" and outranks all
of them. Implemented in `preview.ts` and `hydrate.js`, pinned by a parity test.
The media-player layout links its title rather than the card, because wrapping player
controls in an anchor makes every click a navigation.

**Where the link lands.** `/events/summer-party` had no page: `SiteHost` fell through
to its SPA-style `index.html` fallback and rendered the **home page** at that address.
Added **detail routes** — a page whose route ends in a `:param` segment:

- `outputFileName('/events/:slug')` produces `events_@.html` (`@` cannot occur in a
  real segment, which are kebab-case, so the host can tell "any event" apart from
  "/events/at"); mirrored in `StaticSiteAssembler.FileNameFor`,
- `SiteHost` now tries the exact file, then the same path with its last segment
  wildcarded, then the existing index.html fallback,
- detail routes are left out of the `dcms-routes` table and are not auto-added to the
  nav — `/events/:slug` is not an address,
- `PagesPanel` gained an optional route field (which also fixes never being able to
  create a nested page like `/about/team`), with a hint, and marks detail pages.

`hydrate.js` already read the item slug from the last URL segment for detail-mode
components, so this was the missing link rather than a new mechanism.

## Mode A — clicking a git diff shows the diff

`SourceControlView` called `vfs.openDiff` from the builder's sidebar, but the builder's
`CodeView` rendered only `MonacoEditor` — the diff went into the store and nothing
showed it. `CodeView` now renders diff tabs beside the file tabs and swaps in the
`DiffEditor` when one is active, and `BuilderPage` switches Design to Split when a diff
opens, since Source Control is reachable from a view with nowhere to show it.

## Mode A — resizable split view

The canvas/code split was a hardcoded `w-1/2`. It now uses the same `Resizer` +
`useStoredWidth` the sidebar and inspector use (`dcms.builder.codeWidth`), with a
`max-w-[75%]` so a width stored on a wide screen cannot squeeze the canvas out of
existence on a narrow one.

## Mode A — AGENTS.md in every site repo

`AGENTS.md`, generated into every Mode A repo. That filename because it is the one
coding agents already look for, so it is picked up without configuration.

**Generated, not written.** Every list in it is derived from the declarations the
builder itself uses — the block catalogue and its identity classes, the template
attribute vocabulary, the bind targets, the `#`/`@` namespaces — plus this site's own
pages, components and theme variables. It cannot go stale, and `guide.test.ts` fails
if a block, attribute or bind target is added without appearing in it.

**Permanent.** Written by `projectFiles` (after the `extras` spread, so the generated
copy always wins) *and* by `syncFromVfs`, so merely opening a site puts a current copy
in repos that predate it or that someone deleted it from. Shown read-only in the code
view. Added to the assembler's `SourceOnly` set: it describes the site's internals and
has no business being served at a guessable URL.

## Mode A — scroll slider styling

Three surfaces. The admin shell had `scrollbar-width`/`scrollbar-color`, which WebKit
ignores, so Safari and older Chromium drew the platform default over a dark surface —
added `::-webkit-scrollbar` rules (WebKit drops the standard properties as soon as one
matches, so they never fight). The GrapesJS canvas is its own document, so the admin's
styling stopped at the iframe boundary — added to `canvas.frameStyle`, with fixed
translucent greys rather than theme variables, because the frame resolves `--dcms-*`
from the *site's* theme, which knows nothing about the admin's light/dark mode.
Published Mode A sites got a themed scrollbar in `BLOCKS_CSS`, from theme tokens only
(`kits.test.ts` fails the build on a literal).

## Tag system

**Reusable.** Tag suggestions existed but were scoped to one field of one content type,
which quietly guaranteed every collection would grow its own spelling of the same word.
`TagQueries` (new, in `Dcms.Shared.Data.Cms`) answers "which tags does this tenant use"
across everything, and the admin endpoint's arguments are now all optional — with none,
it is the tenant's whole vocabulary. The picker offers the field's own tags first, then
the rest of the tenant's. Tags differing only in case are grouped and reported under
the spelling used most.

Two things the old query missed, both fixed: a plugin's **tenant-defined** fields are
nested under one key, so a top-level scan walked straight past `values.genres` — the
one tag input in the app with no suggestions at all was the one a tenant added itself;
and custom `tags` fields in `ContentEditor` were never handed suggestions.

**Filtrable.** The admin content list gained a tag filter, built from the rows it has
already loaded (no request, and the offered tags are exactly the ones present).

**Browseable, over the API.** New `GET /api/tags` on the delivery API: every tag the
tenant publishes, most used first, each with the collections and fields it appears in
and a **ready-made delivery call** for that combination — the point being that a site
can be built against it without reading the service's source. Published content only.
`GET /api/{slug}/{contentType}` gained `?tag=` and `?tagField=`, resolved to item ids
first so listing still goes through one reader with one cache and one mapping. A
tag-filtered page is not cached: folding an arbitrary id set into the key would make
one cache entry per tag combination.

**Only when tags are present.** `OpenApiAssembler.Build` takes a `tagging` flag: with
it, the document gains the `/api/tags` path (fully described, with a response schema)
and `tag`/`tagField` parameters on every *list* operation — not on fetch-by-slug, where
the filter could only ever return the item or nothing. Without it, neither appears. The
flag is cached separately from the document and folded into its cache key, because it
is consulted even on a cache hit and the spec must change the moment a tenant publishes
its first tag.

## Verified

`dotnet build Dcms.sln` clean; `dotnet test Dcms.sln` — 20 unit, 48 plugin-SDK
(5 new), 46 integration (5 new). `tsc -b` and `vite build` clean.
`@dcms/gjs-blocks` 706 tests (8 new), `@dcms/gjs-schema` 56 (3 new).

One stale test fixed along the way: `site.test.ts` still asserted the
pre-`cookieConsent` settings default from round 1.


## Deployed — 2026-08-19 23:05 UTC

Synced with tar-over-ssh (`src apps/admin packages pnpm-lock.yaml`), tree-diffed
against the remote (nothing missing), then `run-deploy-full.sh`, which covers exactly
the five services this round touches. No migration; the tag queries are raw SQL over
existing tables. Identity was untouched, so its omission from the script is fine.

### A defect the live data caught

The first deploy's `/api/tags` came back full of GUIDs. Nothing in the stored data
says "this field holds tags" — a `MediaRef[]` is an array of strings exactly like a
`Tags[]` is — so the "array of short scalars" heuristic indexed every photo on the
site as a tag. On one tenant the entire vocabulary was asset ids.

Tightened to exclude the shapes a tag provably never has: a GUID, and anything
starting with `/` or `http`. Deliberately a rule about the **value**, not the field
name: guessing at `photos`/`images` would misfire in both directions on a tenant that
names a tag field something unexpected. The same rule is mirrored in the SPA's
client-side list filter (`looksLikeATag` in `ContentPage.tsx`).

The new SQL was run directly against the production database *before* rebuilding —
four real tags, no GUIDs. Redeployed; `moordoor` now indexes 2 tags, the gallery
tenant 0.

### Verified in production

- `/api/tags` 200, with occurrences and ready-made delivery URLs. The nested-field
  fix works on real data: `custom.specialties` is found, which is precisely the
  tenant-defined-field case that was invisible before.
- `?tag=Neurofunk&tagField=genres` returns the item; an unknown tag returns
  `totalCount: 0`.
- **Only when tags are present**, end to end: the tenant with tags advertises
  `/api/tags` and a `Tags` group; the tenant whose only arrays were media ids does
  not. The flag flipped after the fix and the document's cache key self-invalidated,
  which is the behaviour the key was designed for.
- OpenAPI: `tag`/`tagField` on the list operation, absent from fetch-by-slug.
- site-host: page 200, deep URL still falls back to index (no `:slug` page published
  yet), missing asset still 404s — the wildcard lookup regressed nothing.
- Admin SPA 200; `/api/admin/content/tags` 401 (exists, authenticated).
- All five containers healthy; logs clean.

### ⚠️ Pre-existing bug found, NOT fixed

`site-builder` logs `42501: permission denied for schema plugins`. This is round 1's
`AnalyticsEnabledAsync`, which reads `plugins.plugin_instances` — but
`dcms_sitebuilder` is a least-privilege role with USAGE on `sites` only
(`has_schema_privilege` says f for cms/plugins/tenancy/analytics). The call is wrapped
in a try/catch defaulting to `true`, so publishes succeed and nothing is broken
visibly.

What it means: **"a site with analytics off shows no cookie banner" has never worked
in production.** Every site is treated as analytics-enabled and gets a banner.

Two fixes, neither applied — widening a deliberately-restricted role on a production
database is the owner's call:

1. `GRANT USAGE ON SCHEMA plugins` + `SELECT ON plugins.plugin_instances` to
   `dcms_sitebuilder`. One line, trivially revocable, but widens least privilege for
   one boolean.
2. Put `analyticsEnabled` on the publish message, computed by admin-api, which
   already holds those permissions. Better design, keeps site-builder's privilege
   boundary intact; costs a message-contract change.

Recommendation: (2), as a backlog item rather than a hotfix.

### Housekeeping noticed, not touched

vps1 carries a stray copy of the admin SPA directly under `~/baas-dcms/src/`
(`src/app/`, `src/features/`, `src/components/`, …, dated 20 July) — a mis-targeted
tar sync from months ago, including a whole pre-refactor `features/ide/` tree. Inert:
the .NET SDK only globs `.cs`, and `tsc -b` compiles `apps/admin/src`. Left in place.

---

# Follow-up — analytics flag moved onto the publish message

Fixes the pre-existing bug found during the round-2 deploy: site-builder could not
read `plugins.plugin_instances` (42501), so `AnalyticsEnabledAsync` failed on every
publish, was swallowed, and every site shipped a cookie banner regardless of whether
its tenant recorded anything.

Chosen over granting `dcms_sitebuilder` access to the `plugins` schema: the role is
deliberately least-privilege, and the producer already holds the permission, so
nothing has to be widened.

**Contract.** `SitePublishRequested` gains `bool? AnalyticsEnabled = null`. Nullable
and defaulted so a message already sitting in the JetStream from before the field
existed still deserializes; `null` means "not stated", which the consumer reads as
enabled — erring towards asking rather than tracking, exactly as the old catch-all
fallback did. `Version` stays 1: the change is additive and backward compatible.

**Producer.** One `AnalyticsEnabledAsync` helper in `SiteEndpoints`, used by all
three places that enqueue a build (the publish endpoint, the git-push webhook, and
`EnqueueReleaseBuildAsync` behind both the release-deploy and rebuild endpoints).
`CmsDbContext` is injected into each; the webhook is anonymous and cross-tenant, so
the query filter is bypassed and the tenant matched explicitly.

**Consumer.** `PrerenderAsync` takes the flag from the job and uses
`analyticsEnabled ?? true`. `AnalyticsEnabledAsync` is gone, and with it
site-builder's `CmsDbContext` registration — the service now registers only the one
schema its role can actually reach, with a comment saying so, so the next person to
need a tenant fact puts it on the message instead of adding a DbContext that will
fail the same way.

**Tests.** Three, in `Dcms.UnitTests/Messaging/SitePublishRequestedTests.cs`. The
bug was invisible for a whole release — publishes succeeded and the only symptom was
an unread log line — so what gets pinned is exactly what made it invisible: the flag
survives the wire in both states, and a legacy payload with no field at all decodes
to `null` and therefore to "enabled", rather than to the `false` a bare `default`
would give (which would be the same bug with the consent flipped).

---

# Mode B: analytics + cookie consent in the starter

Mode A sites have collected analytics behind a consent gate since round 1
(`hydrate.js`). Mode B sites collected **nothing** — a React bundle has no
`hydrate.js` and no publish-time hook. This closes that.

## The layer

`src/dcms/` in the React template, a sibling of `src/api/`:

- `analytics.ts` — the collector.
- `CookieConsent.tsx` — the banner.
- `index.ts` — the barrel.

Wired into the default page: `installAnalytics()` in `src/main.tsx`,
`<CookieConsent />` in `src/App.tsx`. Both are one line, both are in files the
author owns, so the wiring is visible and removable.

**Same wire contract as Mode A** — one beacon shape, one consent key
(`dcms-consent`), one session key (`dcms-sid`), one `/api/collect` endpoint. That
is deliberate: a tenant's numbers have to mean the same thing however their site
was built, and a visitor who answered the banner on one shouldn't be asked again
on the other.

Two things genuinely differ, and they are why this is a module rather than a
`<script>` tag:

1. **A SPA never reloads the document.** A React router changes the URL without a
   navigation, so a per-document pageview records the entry page and nothing
   else. `pushState`/`replaceState` are wrapped and `popstate` listened for; the
   beacon is deferred to a microtask because a router updates the URL *before* it
   renders, and a pageview belongs to the page that actually appeared.
2. **A static bundle has no publish-time hook.** Mode A is stamped with the
   tenant's analytics state by the assembler. Mode B has to ask — hence the new
   `GET /api/analytics/status` (below).

**Nothing is stored or sent before consent.** `send()` is the only exit and it
gates on `allowed()`, so declining doesn't merely hide the banner — the session id
is never created. Storage being blocked entirely (private mode) reads as a
refusal: an answer can't be recorded, so one must not be assumed.

## `GET /api/analytics/status`

New public endpoint on content-api, `{ enabled: bool }`, tenant resolved from the
host, cached 5 minutes.

The beacon does not need it — `/api/collect` already no-ops server-side when
analytics is off. It exists purely so the **banner** can be suppressed: without
it, a Mode B site would show a cookie notice on a tenant that stores nothing,
which is the exact theatre the consent work set out to avoid. Until it answers,
`recording` is null — no beacon, no banner. An unreachable API fails closed.

## "Refresh API" now carries it

The button already existed and already did what was asked ("loads fresh API
definitions from dcms"); what changed is its *scope*. `regenerate` filtered on
`src/api/`; it now filters on `GeneratedPrefixes = ["src/api/", "src/dcms/"]`.

The reason `src/dcms/` belongs in the generated set is the same reason `src/api/`
does: it has to keep speaking whatever `/api/collect` currently expects, and a
site scaffolded a year ago should pick up a fix without being rebuilt. Both are
documented in-file as generated, and both take their configuration as arguments —
`installAnalytics(options)`, `<CookieConsent {...props} />` — so nothing an author
wants to change lives in an overwritten file. The button's tooltip now says so.

The "empty" starter flavour is left minimal on purpose — that is what it is for.

## Verified

`tsc --noEmit` and `vite build` clean on the template (37 modules, 200 kB).
`dotnet build` clean. `dotnet test` — 23 unit, 48 plugin-SDK, 47 integration
(1 new: the starter ships the layer, speaks the Mode A contract, tracks route
changes, and the default page actually wires it up — a collector nobody calls is
worse than none, because it looks done).
