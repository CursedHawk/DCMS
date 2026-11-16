# Sitemaps and robots.txt

Every hosted site answers `/sitemap.xml` and `/robots.txt` without any setup (#343). A
`sitemap.xml` or `robots.txt` the site publishes itself always wins.

- **How it works.** Each build stores its page list (`_dcms/pages.json`, never served). site-host
  makes `sitemap.xml` from it on request, under the domain asked: static pages as they are, and a
  detail page (`/events/:slug`) once per published item of the plugin content it names, through the
  same public list a visitor's browser uses. Kept ten minutes per build and domain, at most 50,000
  URLs. `robots.txt` names the sitemap.
- **Left out.** Pages marked noindex, pages User Authentication gates (the edge tells site-host the
  gated prefixes), items a visitor cannot list. A publishing branch's domain has no sitemap and a
  `robots.txt` that disallows everything.
- **A new page list needs a new build.** A build from before a site's pages were read lists only
  `/`; Redeploy it.

## Managing what search engines see (#648)

Every site editor has a **Search engines** tab in its left rail (the IDE, the HTML builder, the
visual builder; on a Mode C site it is a section of its page). It lists the pages the last
published build put in the sitemap, each with a switch, and saves one set of settings per site:

- **Allow search engines to index this site.** Off: every page answers `X-Robots-Tag: noindex,
  nofollow`, `robots.txt` disallows everything and `/sitemap.xml` is 404 — as on a branch domain.
- **Hidden from search engines** — rules for pages left out of the sitemap *and* answered
  `X-Robots-Tag: noindex`, so a page found through a link stays out too:
  `/about` (one page), `/blog/:slug` (every item of that route — its items are not even listed),
  `/private/*` (that path and everything under it). Compared without case. A page's switch adds or
  removes its own rule; a page hidden by a broader rule says which.
- **Extra URLs** — absolute paths the sitemap lists besides those it finds (a page the build cannot
  see, such as a JSX `<Routes>` page of a Mode B app).

Stored on the site (`sites.sites.SearchSettingsJson`, `SiteSearchSettings`), checked on save
(`PUT /api/admin/sites/{id}/search`, `site:publish`, audited `site.search.changed`), and applied by
site-host on every request — **no rebuild**. Saving publishes `site.settings.changed`, which drops
every cached route of the site; what remains is the HTTP cache (60 s for pages, 300 s for the
sitemap). A rollback (activating an earlier build) publishes it too.

The settings travel with the site in deployments (the `sites` kind); where production leaves sites
to deployments, change them in the environment that deploys there. The console assistant has
`get_site_search` / `set_site_search` (the latter asks first: engines drop what they are told to).

## Where each kind of site's pages come from

| Mode | Pages |
| --- | --- |
| A (builder HTML) | `site.json`'s pages; a detail page by its `item` binding. See [mode-a-builder.md](mode-a-builder.md). |
| B (React app) | The `createBrowserRouter([...])` array in `src/routes.tsx`, read at publish (below). |
| C (uploaded files) | Every `.html` file, at its path. |
| D (visual) | `dcms/app.json`'s routes; a detail route by its page's plugin source. |

## Mode B

After `vite build`, site-builder runs one platform script (`src/Services/Dcms.SiteBuilder/Runtime/
sitemap-routes.mjs`) in the same sandbox, with no network. It loads `src/routes.tsx` with the site's
own Vite and catches the route array given to `createBrowserRouter`/`createHashRouter` — no browser,
nothing rendered. The build log says what it found ("Sitemap: 7 page(s) from src/routes.tsx."). If
the routes cannot be loaded outside a browser (code that touches `window` when imported), the
sitemap lists `/` and the build still succeeds. JSX `<Routes>` is not read.

A route says more with React Router's `handle`:

```tsx
{ path: 'blog/:slug', element: <Post />, handle: { sitemap: { instance: 'blog', contentType: 'post' } } }
{ path: 'account', element: <Account />, handle: { sitemap: false } }          // it and its children stay out
{ path: 'press', element: <Press />, handle: { sitemap: [{ path: '/press/2026' }] } } // these instead
```

Static paths are listed as they are; a parameter route only with a binding, its parameter last. The
starter template's `:instance/:contentType` and `:instance/:contentType/:slug` routes list every
collection in the generated `collections` and its items.
