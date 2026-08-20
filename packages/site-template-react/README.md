# @dcms/site-template-react

A minimal Vite + React + TypeScript starter for a DCMS tenant site. It is wired to
the tenant content API through `src/api` and the `@dcms/api-client` runtime.

## How it's used

This package is the canonical scaffold. The admin app embeds it and, on
**API Docs → Download React starter**, materializes a per-tenant copy where:

- `src/api/` is replaced by the tenant's **generated, fully-typed** client
  (`api.<instanceSlug>.<contentType>.list()`), self-contained (runtime vendored under
  `src/api/runtime/`), so the download needs only the pinned React/Vite toolchain.
- `.env` is pre-filled with the tenant's domain and a demo instance.

## Analytics and cookie consent

`src/dcms/` holds the DCMS runtime layer, wired up in `src/main.tsx`
(`installAnalytics()`) and `src/App.tsx` (`<CookieConsent />`).

**Nothing is stored or sent until the visitor accepts.** The banner only appears
when there is something to consent to — the tenant has analytics enabled and this
visitor has not answered — so a site that records nothing shows no notice. Declining
does not merely hide the banner: no session id is ever created.

It reports the same events as a prerendered Mode A site (pageview, outbound click,
download, engagement) to the same `/api/collect` endpoint, so a tenant's numbers mean
the same thing however their site is built. The one addition is client-side route
changes, which a SPA has to report itself because the document never reloads.

Customise it through arguments, not by editing the files:

```tsx
installAnalytics({ apiBaseUrl: config.apiBaseUrl, mode: 'banner' });
<CookieConsent policyUrl="/privacy" message="…" />
```

Use `trackEvent('signup', { plan: 'pro' })` for your own events.

> `src/api/` and `src/dcms/` are both **generated**. The editor's **Refresh API**
> button re-pulls them from DCMS, overwriting local edits. Everything else is yours.

In this workspace copy, `src/api` re-exports the generic `@dcms/api-client` runtime so
the template builds standalone; both expose the same `createTenantClient`, so the app
code is identical either way.

## Develop

```bash
pnpm install
cp .env.example .env   # set VITE_DEMO_SLUG / VITE_DEMO_CONTENT_TYPE (see the admin API Docs page)
pnpm dev
```

`pnpm build` runs `tsc --noEmit && vite build`. Content is fetched with
`api.content(slug, contentType).list()` (generic) or, in a downloaded copy, the typed
`api.<slug>.<contentType>` tree.
