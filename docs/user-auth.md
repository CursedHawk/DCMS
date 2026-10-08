# User Authentication

Enterprise sign-in for a workspace's sites: the people who may sign in, how they sign in, which
pages need it, and what they may do with the workspace's apps once in. The design record is
[ADR 0022](adr/0022-user-authentication.md).

## What it is, and what it is not

- **Site users are not console members.** They sign in to the sites, never to the console, and an
  account exists in one workspace only. Someone who is a site user of two workspaces has two
  accounts. A console member who also needs the sites signs in with *DCMS account* (below), which
  links their platform login to a separate site account.
- **Pages are protected at the edge.** A page under a protected path is not served until the
  visitor has signed in (and is in the right group). Hiding a section in the builder is cosmetic;
  protecting its path is not.
- **Data is protected at the API.** A plugin's site API (`/api/{instance}/…`) can be restricted
  to signed-in users or to a role permission. For a React site this, not the page rules, is the
  protection: see [Single-page apps](#single-page-apps-react-sites).
- **Visitor accounts still exist.** Visitor Authentication is the self-registration kind for the
  public. Both can be enabled; plugins that know visitors see enterprise users as visitors too.

## Setting it up

1. Enable **User Authentication** in Plugins. Its page has three screens: *Users*, *Access* and
   *Sign-in*.
2. Give the site a verified domain (Settings → Domains). Sign-in happens on the site's own
   domain; a site without one has nowhere to sign in.
3. On **Sign-in**, choose how people get in:
   - **Email and password** (on by default): invited people set a password.
   - **Google Workspace**: a Google OAuth client; set the Workspace domain to admit only its accounts.
   - **Microsoft Entra ID**: an app registration in one directory (tenant id or domain).
   - **OpenID Connect**: any provider, by issuer URL.
   - **DCMS account**: people sign in with their DCMS login.

   For Google, Entra and OIDC, register the **Redirect URI** the card shows at the provider. The
   client secret is stored encrypted and never shown again.

   *Who gets in*: only invited people, or anyone the provider vouches for at the email domains you
   list (their account is made on first sign-in, into the groups you choose). An email links to an
   existing account only when the provider vouches for it.
4. On **Users**, invite people and make **Groups** (Staff, Partners…).
5. On **Access → Site access**, add path rules per site. They apply in order; the first whose path
   covers a page decides: *Anyone*, *Any signed-in user*, or *Members of groups*. A page no rule
   covers is public. `/portal` covers `/portal` and everything under it, not `/portals`.
   Put exceptions first: `/portal/news` *Anyone* above `/portal` *Any signed-in user*.
6. On **Access → Roles**, give groups or people permissions on your apps: each published Dynamic
   Apps table's read, create, update and delete, and each manual flow's run. On a table with row
   access rules a role's permission still only reaches the rows the rules give its holders; tick
   **Bypass row-level access** on a role (managers, admins) to let it reach every row. See
   [dynamic-apps.md](dynamic-apps.md#records-and-the-public-api).

7. On **Access → API access**, restrict the plugin instances whose data needs it: *Any
   signed-in user*, or *Role permission* — then give roles `{plugin}:{instance}:api:read` (GET)
   and `…:api:write` (everything else). Restricted instances also drop out of search and
   `/api/tags` for whoever may not read them.

## Single-page apps (React sites)

A React site (Mode B and the visual builder) is downloaded once; moving between its pages is not
a request, so **the edge never sees it**. A rule on `/reports` stops someone opening `/reports`
directly, but not clicking to it inside the app. So, for a React site:

1. **Protect the data, on Access → API access.** Whatever the page shows comes from
   `/api/{instance}/…`, and that is checked on every call, however the page was reached. The
   site rules editor names the instances a rule's path points at (`/audio-library-1/track` →
   `/api/audio-library-1`) with one click to require sign-in for them.
2. **Let the app ask, for the experience.** Both React runtimes ask the edge before drawing a
   route (`/.edge/site/access`) and, when the answer is no, leave the app for the server's own
   sign-in or "no access" page. Visual sites do it built in; a Mode B site gets
   `siteAccessLoader` in `src/dcms` with **Refresh API** and puts it on its root route (the
   content template does). Sites created before this need that one line in `src/routes.tsx`:
   `{ element: <Layout />, loader: siteAccessLoader, shouldRevalidate: siteAccessRevalidate, … }`.

What stays true however it is configured:

- **Anything in the app's code is public to whoever can load the app.** Every page's code, and
  any text or image imported into a component, is in one bundle under `/assets`. Only data
  fetched from the API can be kept from someone.
- **The `/assets` rule decides who can run the app at all.** Rules are first-match, so with
  `/portal` *Staff* above `/` *Admins*, Staff get the `/portal` page but not the code to draw it —
  a blank page. The editor warns and offers an `/assets` rule wide enough for everyone let in.
- **Client checks are the experience.** `useSiteAccess`, `useSiteUser` and the route guard can be
  bypassed by anyone with the browser's devtools; the edge and the API cannot.
- **Two vocabularies.** Page rules are per domain, by path prefix, for groups; API access is per
  plugin instance, for every domain, by role permission. A prefix cannot express a route pattern
  (`/:id/edit`).
- **Media files are not protected.** An image or audio file of restricted content is still
  served to anyone with its URL (`/api/media/{id}/…`).
- **The live chat is not an instance API.** Its hub is not covered by API access; its bot does
  not answer from restricted content.

## On the site

- Builder blocks (Forms category): **Sign-in button**, **Sign-out button**, and **Signed-in
  section** (shown only to signed-in users, or only to signed-out ones, with the user's name).
- Custom code: `window.dcms.user.current()`, `.signIn(returnUrl)`, `.signOut()`.
- The site API client: `api.siteUser()`, `api.siteSignInUrl()`, `api.siteSignOutUrl()`,
  `api.siteAccess(paths)`; `ApiError.signInRequired` for a session that ended under an open app.
- A role permission from the site: the generated client's `api.{user-auth instance}.access.check({ permission })`
  and `.listPermissions()` (`users.access@1`).
- The edge's own endpoints on every protected site: `/.edge/site/signin?returnUrl=…`,
  `/.edge/site/signout`, `/.edge/site/me`, `/.edge/site/access?path=…` (allow / signin /
  forbidden / unavailable per path). Refusals of API calls, at the edge and by API access, are
  `application/problem+json` with `error` = `signin_required` or `forbidden` and a `signInUrl`.

## Automation and the assistant

- Flow triggers: `user.invited`, `user.activated` (an invitation accepted, or a first sign-in that
  made the account).
- Flow actions: `user-auth.invite@1`, `user-auth.add-to-group@1`, `user-auth.remove-from-group@1`,
  each needing *Manage site users* of whoever publishes the flow.
- The assistant can list, invite, enable or disable users and change their groups
  (`users.directory@1`), when the instance's AI tools are on. Disabling is marked dangerous.

## How long changes take

| Change | Reaches |
| --- | --- |
| A site rule | Every edge at once; at worst within 2 minutes. |
| A role or a grant | The user's next request. |
| An instance's API access | Within 10 seconds. |
| A user's groups, disabling them, "sign out everywhere" | Within 10 minutes (their next token). |
| A domain added to or removed from a site | Within 5 minutes. |

## Operating it

- **`EDGE_SITES_SECRET`** in the host's `.env`: identity and the edge derive every site's sign-in
  client secret from it. `scripts/deploy.sh` generates it on a real host; nothing to do by hand.
  Without it, protected paths answer 503 and the Sign-in screen warns that sign-in is not
  configured.
- Sessions live in Redis (`edge:site:*`, 24 hours idle at most); the cookie holds only an id.
- Until the edge has read the rules once after a start, tenant sites answer 503 and it retries
  every 5 seconds. Rules that cannot be read protect the whole site.
- Deleting a workspace deletes its site users, groups, providers and sign-in client.
