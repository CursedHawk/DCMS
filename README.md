# baas-dcms

Multi-tenant Backend-as-a-Service / Dynamic Content Management System.

Tenants enable **plugins** (gallery, blog, video streaming, search, chat, …)
in a central admin panel, author content through a draft → version → publish
workflow, and host their websites (custom domains) built with a drag-and-drop
editor or AI generation against a per-tenant, AI-readable OpenAPI document.

## Stack

.NET 10 multiservice backend (identity + OpenIddict, admin-api, content-api
with in-process Plugin SDK, media-worker, site-builder, site-host, ai-gateway)
· React/Vite admin SPA · PostgreSQL 18 · NATS JetStream · Redis · MinIO ·
HashiCorp Vault · Docker Compose.

## Quick start

```sh
docker compose up -d --build
./scripts/smoke.ps1
pnpm install && pnpm dev:admin
```

See `docs/runbook.md` for ports/credentials, `docs/plugins.md` for writing
plugins, and `docs/mode-a-builder.md` for the visual builder that authors Mode A
sites. Implementation proceeds in 12 phases (see the project plan); Phase 1
(infrastructure + scaffold) is complete.
