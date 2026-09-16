# Blank project

A React + TypeScript + Vite site with nothing on the page yet, but with the typed API client, the loading hook and the design tokens ready to use.

Read this before changing anything. It is short on purpose.

## Where things are

| Path | What |
| --- | --- |
| `src/main.tsx` | Entry point: installs analytics, renders `App` into `#root`. Keep this wiring. |
| `src/App.tsx` | The page. Start here. |
| `src/site.ts` | Site name and privacy link. |
| `src/lib/api.ts`, `src/lib/useApi.ts` | The API client and the hook to load with it. |

## Common tasks

- **Show content**: `const posts = useApi(() => api.content(c.instance, c.contentType).list(), [])`
  with `c` from `collections` — or the typed accessor listed in API.md.
- **Several pages**: add `react-router` 7.13.0 (already in the preview palette); put routes in
  `src/routes.tsx` and pages in `src/pages/`, so the project reads like the other DCMS templates.
- **Split the page up**: components in `src/components/`, one per file.

## Never edit these — DCMS rewrites them

| Path | What |
| --- | --- |
| `src/api/` | The typed client for this tenant's content API, generated from its plugins. |
| `src/dcms/` | Analytics and the cookie-consent banner. Configure through props and options. |
| `openapi.json` | The raw API description. Large — read `src/api/API.md` instead. |

They are regenerated whenever the tenant's plugins change, so an edit there is lost.
Everything else in the project is yours.

## Getting content

1. **Read `src/api/API.md` first.** It lists every collection, form and call, with field names.
   Do not guess field names or response shapes — the most common failure is a page that builds
   and then renders nothing because it read `item.title` instead of `item.data.title`.
2. Use the one client, `api` from `src/lib/api.ts`. Never call `fetch` for `/api/...` by hand.
3. Load in components with `useApi` from `src/lib/useApi.ts` — it handles loading, errors,
   retries and stale responses. Always render all three states.
4. Items look like `{ id, slug, data, publishedAt }`; the fields are under `data`. Media fields
   hold asset ids: `api.media.url(id, 'webp-960')`.
5. `collections` and `forms` (exported from `src/api`) describe what this tenant has, so code
   written against them works for any tenant. For one specific collection, use its typed accessor,
   e.g. `api.news.post.list({ pageSize: 6 })`.

## Styling

- Plain CSS. Change the look in `src/styles/tokens.css` (colours, fonts, spacing) before
  touching rules; `src/styles/base.css` has the shared pieces (`.container`, `.button`, `.field`).
- Mobile first: every page must work at 360px wide with no horizontal scroll.
- Keep the focus outline, labelled inputs, heading order and `alt` text.

## Preview and production differ in one way

The live preview loads packages from a fixed palette (React 19.2, react-router 7.13, and others);
the published build installs `package.json`. So you **can** add a dependency and it will build,
but the preview may not show it until the site is published — say so when you add one. Keep
versions pinned exactly, as they are now.
