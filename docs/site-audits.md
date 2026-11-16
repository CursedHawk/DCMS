# Site audits (PageSpeed Insights)

Every site editor has an **Audit** tab in its left rail — the IDE (Mode B), the HTML builder
(Mode A), the visual builder (Mode D); a Mode C site has it on its page — and every site agent has
the same audits as tools (#649). An audit asks Google's PageSpeed Insights for **performance,
accessibility, best practices and SEO**, on mobile and desktop.

## Live or unpublished

- **Live site** — the site's primary release domain, as visitors get it. PSI may add real-user
  (field) data when Google has enough traffic for the address.
- **Unpublished** — what the person auditing sees in their editor: their working draft of the
  branch (autosaved, not necessarily committed), else the branch head; for uploaded files, the
  staged upload. It is built as an **audit build** (`site_builds.AuditToken`) — like any build, but
  never made live, not listed among deployments, and announcing nothing — and served for **one hour**
  at `https://audit-{token}.dcms.highgeek.eu/` (128-bit token; the platform wildcard certificate
  covers it), then 404. It is served as it would be live: its search settings, **no forced
  noindex** — a noindex would fail PSI's SEO audit for every draft. Its `/api` calls reach the
  environment's live data, as the editor preview does. The address is never shown in the console.

## How it runs

`POST /api/admin/sites/{id}/audits` (`site:edit`, audited `site.audit.requested`) records a
`sites.site_audits` row (and for a draft queues the audit build), then enqueues job `site.audit`
on the jobs-worker (ADR 0024): wait for the build → PSI mobile → PSI desktop. Each strategy's
result is stored as a summary (`AuditSummary`): category scores, lab metrics, field data, and every
failing audit with its first 10 elements/files and savings. `GET …/audits` lists the last 20;
`GET …/audits/{auditId}` returns one in full. One audit per site runs at a time.

## The agent

- `run_site_audit { target: draft | live }` — saves the run's pending edits, starts the audit,
  waits for its job (the hub-patched jobs cache; Stop ends the wait), and returns scores and the
  failing audits by id.
- `get_site_audit { auditId?, auditRef?, strategy? }` — the latest audit, or one failing audit in
  full (description, elements, files, savings). Its output is fenced as untrusted: it quotes the
  page's own URLs and markup.

The loop: run → read an issue → fix → run again.

## Setup

PSI requires an API key (keyless calls get no quota): create a key in Google Cloud with the
**PageSpeed Insights API** enabled and store it as `PageSpeed__ApiKey` for admin-api in Vault
(see [vault-secrets.md](vault-secrets.md)). Without it the Audit tab says the platform is not set up.
