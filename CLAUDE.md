# DCMS (baas-dcms)

Multi-tenant CMS and site-builder platform. .NET 10 services + React 19 SPAs in one repo.

## Commands

```bash
# .NET
dotnet build                                     # whole solution
dotnet test tests/Dcms.UnitTests                 # fast; always run these
dotnet test tests/Dcms.IntegrationTests --filter "FullyQualifiedName~Tenancy"

# Frontend (pnpm workspace)
pnpm build                 # all packages
pnpm test                  # all vitest suites
pnpm lint                  # eslint, repo-wide
pnpm e2e                   # playwright: real SPAs, mocked server, no compose stack
pnpm --filter @dcms/admin dev
```

This box **runs tests and builds only** — it is not a deploy target. The full
`Dcms.IntegrationTests` suite gets OOM-killed here (7.6 GB, no swap), so always filter it.

## Layout

| Path | What |
| --- | --- |
| `src/Services/` | admin-api, content-api, identity, edge (YARP ingress), site-builder, site-host, ai-gateway, media-worker, email-worker, platform-api |
| `src/Shared/` | Kernel, Data (EF + RLS), Contracts, Messaging (NATS), Security, Vault, Telemetry |
| `src/Plugins/` | content plugins (Blog, Forms, Search, …) behind `Dcms.PluginSdk` |
| `apps/admin`, `apps/platform` | React 19 + Vite SPAs |
| `packages/` | shared UI, api-client, GrapesJS blocks/parse/schema, site template |
| `infra/` | postgres init, vault, nats, observability — all applied by the deploy |

## Shipping

**Commit and push straight to `master`.** No feature branches, no MRs: `deploy:dev` only runs
on the default branch, so work on a side branch does not deploy at all. Upstream is
`gitlab.highgeek.eu`, already configured as `origin`.

**A push to `master` is the entire deploy procedure.** The pipeline runs build → test → images
→ rolls vps1 by digest. Production is a `v*` tag plus a manual `deploy:prod` job.

So anything a change needs done to a host, database or broker goes into a file the deploy
already runs — never into a runbook as a step for a human. Schemas → `infra/postgres/init/`;
new table → EF migration + register the DbContext in `AdminApi/Tenancy/TenancyMigrator.cs`
(both `MigrateAllAsync` and the `AssertRlsCoverage` array); RLS → see below.

Never tar-sync source to vps1, and never run `run-deploy*.sh` — retired, and running one now
reverts a release.

## Gotchas

- **A new tenant table must be added to `RlsConfigurator.TenantTables`**
  (`src/Shared/Dcms.Shared.Data/Rls/RlsConfigurator.cs`). That array is hardcoded, not derived
  from the EF model. Grants are default-ALLOW while policies are opt-in, so a table you forget
  is readable *unfiltered across every tenant* by the `dcms_rls` role. The startup log's
  "applied to N tenant tables" is just the array length — it proves nothing about coverage.
- **Hand-run compose on vps1 needs the full three-file `-f` set** (`docker-compose.yml`
  `-f docker-compose.prod.yml` `-f docker-compose.vps.yml`) and explicit service names. A
  partial set recreated Vault in dev/inmem mode and took admin-api down.
- **Build serially on vps1** (4 CPU / 7.6 GB). Nine parallel service builds drove load average
  to 375 and caused a 30-minute outage; a runaway build cannot be killed without a reboot.
- **`X-Dcms-Tenant` carries the tenant slug, not the uuid.**
- There is deliberately no `latest` image tag.
