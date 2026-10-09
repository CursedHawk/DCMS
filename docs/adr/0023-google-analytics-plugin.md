# ADR 0023: Google Analytics as a plugin: a Measurement ID per site, looked up at runtime, loaded after consent

**Status:** accepted (2026-10-09) · implemented (kanban epic #237)

## Context

Tenants want Google Analytics 4 on their sites. A tenant can own several sites, and each may
report to a different GA property. DCMS already has its own first-party analytics (the
Analytics plugin), a consent banner in both site runtimes, and a dashboard. GA has to sit beside
these without double-counting, and without sending Google anything a visitor did not agree to.

Two things about the existing system shaped the design:
- **The consent banner only knew about the DCMS collector.** Mode A pages are stamped at publish
  with "is the Analytics plugin on" (`SitePublishRequested.AnalyticsEnabled`). React sites ask
  `/api/analytics/status`. A GA-only site would never have asked.
- **Nothing told the backend which site a request came from.** site-host resolved the site from
  the Host but forwarded only the tenant.

## Decision

### A separate, single-instance plugin

`Dcms.Plugins.GoogleAnalytics` (`google-analytics`):
- **Config.** The instance config is `{ "sites": { "<siteId>": "G-…" } }`, validated by the
  manifest schema.
- **Editing.** The plugin's instance screen `sites` edits it: one row per site of the workspace.
  Mode C rows are read-only.
- **Not on the Site row.** GA is optional and third-party, so it lives in a plugin, like every
  other optional feature. Enabling and disabling it is the ordinary plugin switch.

### Looked up at runtime, not stamped at publish

The runtimes ask `GET /api/ga/config`:
- **Answer.** The plugin answers with the Measurement ID of the site in `X-Dcms-Site`, or `{}`.
  Never 404.
- **Who names the site.** site-host sets `X-Dcms-Site` and `X-Dcms-Site-Host` from the route it
  resolved. The edge scrubs both from every inbound request.
- **Caching.** The answer is cached for 5 minutes server-side (per tenant) and in the browser
  (`Cache-Control: max-age=300`).
- **Why runtime.** A new ID, or a disabled plugin, reaches every live site within those 5 minutes
  without republishing any of them. Turning tracking *off* must not wait for a build.
- **Trade-off.** One small cached request per visitor per 5 minutes, on every site of every
  tenant.

### Basic consent mode only

- **Loading.** `gtag.js` is not loaded until the visitor accepts the banner. A visitor who
  declines, or never answers, sends Google nothing.
- **Consent defaults.** On load the tag is given `analytics_storage: granted` and every `ad_*`
  storage `denied`.
- **Not Advanced consent mode.** Advanced mode sends cookieless pings before consent. That is a
  separate product and legal decision.
- **Banner eligibility.** The banner appears when *any* provider is on: the DCMS collector, or a
  GA ID for this site.
- **Banner copy.** It names Google when GA is among them. "Anonymous analytics" stops being true
  once Google is involved. An owner's own banner message still wins.
- **`mode: "off"`** is a site owner's statement that no consent is needed. It applies to GA too.

### Each system counts its own page views

- **GA.** GA records page views itself: its initial `page_view`, plus enhanced measurement's
  "page changes based on browser history events" for single-page sites. The settings screen tells
  owners to keep that on.
- **No forwarding.** The DCMS beacon is never forwarded to GA, and DCMS events are not mapped to
  GA events.
- **Why.** Forwarding page views while enhanced measurement is also on would count every SPA
  navigation twice.

### Mode C stays manual

An uploaded bundle runs no DCMS runtime, so its owner adds the Google tag themselves. The settings
screen says so. Patching uploaded HTML at publish is not done.

### No CSP change

Tenant sites are served with no `Content-Security-Policy` today: `Security:ContentSecurityPolicy`
is unset. If it is ever set for site-host, it must allow:
- `https://www.googletagmanager.com` in `script-src`
- `https://*.google-analytics.com` and `https://*.analytics.google.com` in `connect-src`

## Alongside: DCMS analytics fixes

The same work made the first-party analytics trustworthy:
- **Site attribution.** Events carry `SiteId`/`Hostname`, and rollups key on `SiteId` (an empty
  GUID means unattributed or historical). The dashboard filters by site and domain, and has a
  domains breakdown.
- **Idempotent ingest.**
  - Each hit is stored under its ingest id, unique per tenant.
  - The consumer's insert and rollup upsert commit together, so a JetStream redelivery is a no-op.
  - The publish carries the id as `Nats-Msg-Id`.
- **No query-only page views.** A `replaceState` that only rewrites the query is not a page view.
  The SPA runtime counted every filter or search change.
- **Referrers.** The landing referrer is sent with the first page view only. A referrer on the
  site's own host is dropped at ingest.
- **Automated traffic.** Crawlers (by User-Agent) are dropped at ingest. Automated browsers
  (`navigator.webdriver`) send nothing.
- **Mode D consent.** The Mode D template renders the consent banner. Without it, nothing was ever
  recorded.

## Not done

- GA reports inside DCMS (the GA Data API with OAuth).
- Measurement Protocol / server-side events. They would need a secret, which belongs in Vault and
  never in a page.
- DCMS→GA business events (`generate_lead`, `sign_up`, `login`, `search`).
- A Google "developer ID" for the platform.
- A UI for a visitor to revoke consent later.
