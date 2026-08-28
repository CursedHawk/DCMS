# baas-dcms

Multi-tenant Backend-as-a-Service / Dynamic Content Management System.

Tenants enable **plugins** (gallery, blog, video streaming, search, chat, forms,
…) in a central admin panel, author content through a draft → version → publish
workflow, and host their websites (custom domains) built with a drag-and-drop
editor, a browser IDE, or AI generation against a per-tenant, AI-readable
OpenAPI document.

## Stack

.NET 10 multiservice backend (identity + OpenIddict, admin-api, content-api with
in-process Plugin SDK, media-worker, email-worker, site-builder, site-host,
ai-gateway) · React/Vite admin SPA · PostgreSQL 18 · NATS JetStream · Redis ·
MinIO · HashiCorp Vault · Forgejo (site source of truth) · Caddy · OpenTelemetry
into Grafana/Prometheus/Loki/Tempo · Docker Compose.

Site source lives in git: Mode A sites are HTML/CSS authored with GrapesJS, Mode
B sites are React projects edited in the browser IDE. A push to a repo's
`release` branch builds and deploys it.

## Quick start

```sh
docker compose up -d --build
pwsh scripts/smoke.ps1
pnpm install && pnpm dev:admin
```

## Docs

- `docs/runbook.md` — ports, credentials and per-feature configuration
- `docs/deploy-linux.md` — dev, production and VPS deployment
- `docs/plugins.md` — writing a plugin
- `docs/mode-a-builder.md` — the GrapesJS visual builder
- `docs/adr/` — architecture decision records
- `infra/observability/README.md` — the telemetry stack
- `TODO/PROGRESS.md` — working notes, newest round last
