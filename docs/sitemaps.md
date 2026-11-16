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
