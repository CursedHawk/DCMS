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
   Apps table's read, create, update and delete, and each manual flow's run. See
   [dynamic-apps.md](dynamic-apps.md#records-and-the-public-api).

## On the site

- Builder blocks (Forms category): **Sign-in button**, **Sign-out button**, and **Signed-in
  section** (shown only to signed-in users, or only to signed-out ones, with the user's name).
- Custom code: `window.dcms.user.current()`, `.signIn(returnUrl)`, `.signOut()`.
- The site API client: `api.siteUser()`, `api.siteSignInUrl()`, `api.siteSignOutUrl()`.
- The edge's own endpoints on every protected site: `/.edge/site/signin?returnUrl=…`,
  `/.edge/site/signout`, `/.edge/site/me`.

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
