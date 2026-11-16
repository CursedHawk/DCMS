# Sitemaps and robots.txt

Every hosted site answers `/robots.txt`, and `/sitemap.xml` when it has something to list. A
`sitemap.xml` or `robots.txt` the site publishes itself always wins.

## What a sitemap lists

- **A Mode D (visual) site lists its own pages** (#343): its build stores a page list from
  `dcms/app.json`'s routes (less noindex pages; `_dcms/pages.json`, never served), and site-host
  lists each detail route (`/events/:slug`) once per published item of the plugin content its page
  shows, through the same public list a visitor's browser uses. Pages User Authentication gates are
  left out (the edge tells site-host the gated prefixes). A Mode D build from before 2026-11-16 lists
  only `/`; Redeploy it.
- **Every other site's sitemap is exactly what its people enter** (#648) in the console. Mode A, B
  and C sites list nothing by themselves — reading routes out of code proved unreliable, and was
  removed. With no URLs entered such a site has no sitemap (404), and its `robots.txt` names none.

Made on request under the domain asked, kept ten minutes per build, domain and settings, at most
50,000 URLs.

## Managing what search engines see (#648)

Every site editor has a **Search engines** tab in its left rail (the IDE, the HTML builder, the
visual builder; on a Mode C site it is a section of its page). One set of settings per site:

- **Allow search engines to index this site.** Off: every page answers `X-Robots-Tag: noindex,
  nofollow`, `robots.txt` disallows everything and `/sitemap.xml` is 404 — as on a branch domain.
- **Sitemap URLs** (on a Mode D site, **Extra URLs**, besides its own pages) — absolute paths the
  sitemap lists.
- **Hidden from search engines** — rules for pages left out of the sitemap *and* answered
  `X-Robots-Tag: noindex`, so a page found through a link stays out too: `/about` (one page),
  `/blog/:slug` (every item of that route), `/private/*` (that path and everything under it).
  Compared without case. On a Mode D site each of its own pages also has a switch, which adds or
  removes that page's own rule; a page hidden by a broader rule says which.

Stored on the site (`sites.sites.SearchSettingsJson`, `SiteSearchSettings`), checked on save
(`PUT /api/admin/sites/{id}/search`, `site:publish`, audited `site.search.changed`), and applied by
site-host on every request — **no rebuild**. Saving publishes `site.settings.changed`, which drops
every cached route of the site; what remains is the HTTP cache (60 s for pages, 300 s for the
sitemap). A rollback (activating an earlier build) publishes it too.

The settings travel with the site in deployments (the `sites` kind); where production leaves sites
to deployments, change them in the environment that deploys there. The console assistant has
`get_site_search` / `set_site_search` (the latter asks first: engines drop what they are told to).

A publishing branch's domain has no sitemap and a `robots.txt` that disallows everything.
