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

## Quick start (local development only)

```sh
docker compose up -d --build
pwsh scripts/smoke.ps1
pnpm install && pnpm dev:admin
```

This is the local stack: Vault in `-dev` mode, no TLS, development defaults throughout. A
deployed host is a different shape entirely — images pulled by digest, per-service Vault
AppRoles, Transit auto-unseal — and is covered in [`docs/setup.md`](docs/setup.md).

## Docs

**Setting the platform up? Start with [`docs/setup.md`](docs/setup.md)** — all three hosts,
the Vault chain and the CI/CD pipeline, from nothing to a host that deploys itself.

- `docs/setup.md` — **full platform setup**, ops + dev + prod
- `docs/vault-secrets.md` — what goes in Vault, where, and what cannot go there yet
- `docs/seal-vault.md` — the Vault that auto-unseals the others
- `docs/runbook.md` — ports, credentials and per-feature configuration
- `docs/deploy-linux.md` — single-host bring-up in detail
- `docs/plugins.md` — writing a plugin
- `docs/mode-a-builder.md` — the GrapesJS visual builder
- `docs/adr/` — architecture decision records
- `infra/observability/README.md` — the telemetry stack
- `TODO/PROGRESS.md` — working notes, newest round last
