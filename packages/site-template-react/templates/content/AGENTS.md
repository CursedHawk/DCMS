# Content site

A multi-page site that shows the tenant's published content: a home page with the latest items from every collection, a paginated page per collection (with tag filtering), and a page per item.

Read this before changing anything. It is short on purpose.

## Where things are

| Path | What |
| --- | --- |
| `src/routes.tsx` | Every page and its URL. **Add pages here.** |
| `src/pages/` | One component per page. |
| `src/components/Layout.tsx` | Header, navigation, footer — wraps every page. |
| `src/components/` | Reusable pieces: `ItemCard`, `RichText`, `Status` (loading/error/empty). |
| `src/lib/content.ts` | Reads an item's title, image, body, date through its collection's field hints. |
| `src/site.ts` | Site name, tagline, privacy link. |

## Common tasks

- **New page**: create `src/pages/AboutPage.tsx`, add `{ path: 'about', element: <AboutPage /> }`
  to the children in `src/routes.tsx`, and a `NavLink` in `Layout.tsx` if it belongs in the menu.
  Static paths win over the `:instance/:contentType` routes, so order does not matter.
- **A dedicated page for one collection**: copy `CollectionPage.tsx`, use the typed accessor from
  API.md, and render its real fields rather than the generic helpers.
- **Links**: use `<Link to>` / `<NavLink to>` from `react-router`, never `<a href>` for internal
  pages — a full reload drops app state.
- **Preview navigation**: the preview runs routes in memory starting at `/`, so the address bar
  does not change and a deep link cannot be opened directly. Clicking through links works.

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
