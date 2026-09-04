# Deploying baas-dcms on Linux with Docker

> **Setting up the platform from scratch? Start with [`setup.md`](setup.md).** It covers all
> three hosts, the Vault chain and the pipeline. This document is the single-host detail it
> refers to, and assumes you are bringing a host up by hand.


This guide deploys the whole system — 8 .NET services, the admin SPA, and the
infra (Postgres, Redis, NATS JetStream, MinIO, Vault, Forgejo, Caddy, and the
Alloy/Prometheus/Loki/Tempo/Grafana telemetry stack) — on a single Linux host
using Docker Compose. It covers a **quick start** (dev/staging, all-in-one) and a
**production deployment** (real Vault, Caddy TLS for tenant domains, secrets from
the environment).

Three compose files matter: `docker-compose.yml` is the base, `docker-compose.prod.yml`
is the production profile, and `docker-compose.vps.yml` carries the host-specific
overrides for the managed VPS.

> **Every production invocation passes all three `-f` files.** A partial set does
> not error — it silently drops the overrides, which is how a previous deploy on
> this host caused an outage. Define the invocation once per shell session and
> use the variable everywhere:
>
> ```bash
> C="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
> ```
>
> Every `$C` below expands to that. On a host with no `docker-compose.vps.yml`,
> drop that one file and keep the other two.
>
> **Prefer `scripts/deploy.sh`.** It resolves the overlay set for you, builds
> serially, force-recreates the services whose config is a bind-mounted file, and
> health-gates the result. The raw `$C` commands here are for when you need to do
> something the script does not cover. Validate any change with
> `scripts/deploy.sh --check` first — it resolves and validates the full overlay
> set and changes nothing.

Everything is built from source by Compose; you do not need the .NET SDK or
Node on the host — only Docker.

---

## 0. Prerequisites

On the Linux host:

```bash
# Docker Engine + the Compose v2 plugin (Ubuntu/Debian example)
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker "$USER"   # log out/in so `docker` works without sudo
docker version && docker compose version
```

- **Resources:** the build compiles 8 .NET services + a Node/Vite SPA and runs
  the infra and telemetry containers. Give the host at least **4 vCPU / 8 GB RAM /
  20 GB disk**. The site-builder image also bundles Node + ffmpeg-adjacent
  toolchains. On a 4-core host, build services **serially** — building more than
  two at once thrashes the box.
- **Ports:** dev exposes `5000-5010`, `5432`, `6379`, `4222/8222`, `9000/9001`,
  `8200`, `8025`. Prod exposes only `80`/`443` (Caddy) plus whatever you choose
  for the admin/identity ingress.
- **Outbound network at build time** (NuGet, npm, Debian/Alpine packages).

Get the code:

```bash
git clone <your-remote>/baas-dcms.git
cd baas-dcms
```

---

## 1. Quick start (dev / staging — single command)

The base `docker-compose.yml` + the auto-applied `docker-compose.override.yml`
give you the full stack with dev credentials and host ports. Vault runs in dev
mode and is auto-seeded; NATS streams and MinIO buckets are auto-provisioned.

```bash
docker compose up -d --build
```

First boot does a lot (image builds + EF migrations + provisioning jobs), so give
it a few minutes. Watch progress:

```bash
docker compose ps
docker compose logs -f admin-api      # "All databases migrated." + "RLS policies applied"
docker compose logs -f nats-init minio-init vault-init   # one-shot provisioners
```

### 1a. The OIDC issuer hostname (important)

Identity issues tokens under the alias `dcms-identity:8080` so the browser and
the backend resolve the **same** issuer. For browser login to work, add a hosts
entry on the machine running the browser:

```bash
echo "127.0.0.1 dcms-identity" | sudo tee -a /etc/hosts
```

(If you're hitting the box remotely, point `dcms-identity` at the server's IP
instead, or skip the SPA and exercise the APIs directly.)

### 1b. Verify

```bash
# Health of every service + provisioning (streams/buckets/Vault KV)
pwsh scripts/smoke.ps1            # if PowerShell is installed
# …or check manually:
curl -fsS localhost:5002/health   # admin-api
curl -fsS localhost:5003/health   # content-api
curl -fsS localhost:5003/api/_plugins | jq length   # → 16
```

Dev endpoints: admin SPA `:5000`, identity `:5001`, admin-api `:5002`,
content-api `:5003`, media-worker `:5004`, site-builder `:5005`, site-host
`:5006`, ai-gateway `:5007`, platform-api `:5008`, platform-spa `:5010`,
Postgres `:5432`, Redis `:6379`, NATS `:4222`
(monitor `:8222`), MinIO `:9000` (console `:9001`), Vault `:8200`, Mailpit `:8025`.
Seeded platform admin: `admin@dcms.local` / `Admin!23456`.

To stop / reset:

```bash
docker compose down           # stop, keep data
docker compose down -v        # stop and delete all volumes (full reset)
```

---

## 2. Production deployment

The prod profile (`docker-compose.prod.yml`) is used **instead of** the dev
override and changes the security-relevant pieces:

- **Caddy** is the public ingress and terminates TLS for tenant custom domains
  (on-demand TLS, gated by site-host so certs are only issued for verified
  domains).
- **Vault** runs in real server mode (file storage, **no dev root token**); the
  services refuse to start against a dev token (`DCMS_REFUSE_DEV_VAULT=true`).
- All secrets come from the host environment — no dev defaults are baked in.
- Services run in the `Production` environment with HTTPS metadata required.

### 2.1 Create the environment file

Compose reads `.env` from the project directory for `${VAR}` substitution.

```bash
cat > .env <<'EOF'
# Database / object store / messaging
POSTGRES_PASSWORD=<long-random-password>
MINIO_ROOT_USER=dcms
MINIO_ROOT_PASSWORD=<long-random-password>

# Vault token the services use to read config (provisioned in step 2.3)
VAULT_TOKEN=<vault-token-with-read-on-secret/dcms/*-and-transit>

# OAuth confidential client secret (admin-api → ai-gateway, must match Vault/identity seed)
ADMIN_API_CLIENT_SECRET=<long-random-secret>

# Public base URL of the identity/admin plane (used as the token issuer)
PUBLIC_BASE_URL=https://admin.example.com

# Let's Encrypt account email for Caddy on-demand TLS
ACME_EMAIL=ops@example.com

# Platform SuperAdmin seeded by identity on first boot
SUPERADMIN_EMAIL=admin@example.com
SUPERADMIN_PASSWORD=<long-random-password>

# Forgejo git backend. The webhook secret gates the anonymous build-on-push
# route: admin-api REFUSES TO START in Production if it is empty or a known
# default, so set a strong one and re-register the site webhooks after changing it.
FORGEJO_TOKEN=<forgejo-machine-token>
FORGEJO_ADMIN_TOKEN=<forgejo-token-with-write:admin>
FORGEJO_WEBHOOK_SECRET=<long-random-secret>

# Grafana alert webhook -> admin-api -> email queue. Must match on both sides.
# Leave unset to disable alert delivery; if set it must be >= 16 chars and not a
# known default, or admin-api refuses to start in Production.
ALERT_WEBHOOK_SECRET=<long-random-secret>
ALERT_RECIPIENTS=ops@example.com

# SMTP relay. email-worker is the sole holder of these credentials.
EMAIL_PASSWORD=<smtp-password>

# Observability
GRAFANA_ADMIN_PASSWORD=<long-random-password>
EDGE_OIDC_CLIENT_SECRET=<long-random-secret>
GRAFANA_DB_PASSWORD=<long-random-password>

# NATS accounts
NATS_APP_PASSWORD=<long-random-password>
NATS_SYS_PASSWORD=<long-random-password>
EOF
chmod 600 .env
```

Generate strong values with `openssl rand -base64 36`.

### 2.2 Bring up infrastructure first

Start the data/infra tier so you can initialise Vault before the app services
boot (they read config from Vault at startup):

```bash
$C up -d --build \
  postgres redis nats minio vault
```

The `nats-init` and `minio-init` provisioners run automatically (they create the
8 JetStream streams and the `dcms-media` / `dcms-sites` / `dcms-build-logs`
buckets). `vault-init` is a **no-op** in prod — you provision Vault yourself next.

### 2.3 Initialise and provision Vault

Prod Vault starts **sealed and empty**. Initialise, unseal, and recreate the
secret layout (mirrors `infra/vault/init.sh`).

```bash
# Operator-init (do this once; store the unseal keys + root token SECURELY/offline)
docker compose exec vault vault operator init -key-shares=1 -key-threshold=1

# Unseal (repeat for each key share; here key-threshold=1)
docker compose exec vault vault operator unseal <UNSEAL_KEY>

# Log in with the root token from `operator init`
docker compose exec vault vault login <ROOT_TOKEN>
```

Now provision the paths the services expect (run inside the vault container, or
set `VAULT_ADDR`/`VAULT_TOKEN` locally and run the `vault` CLI):

```bash
# KV v2 (mounted at secret/ by default), one path per service
docker compose exec vault sh -c '
  vault kv put secret/dcms/shared      placeholder=true
  vault kv put secret/dcms/identity    Identity__SuperAdmin__Email=admin@example.com Identity__SuperAdmin__Password=<strong>
  vault kv put secret/dcms/admin-api   placeholder=true
  vault kv put secret/dcms/content-api Visitor__SigningKey=<32+ byte random>
  vault kv put secret/dcms/media-worker placeholder=true
  vault kv put secret/dcms/site-builder placeholder=true
  vault kv put secret/dcms/site-host   placeholder=true
  # Global AI defaults (set a real Ai__Defaults__ApiKey if you want a platform fallback key)
  vault kv put secret/dcms/ai-gateway \
      Ai__Defaults__Provider=anthropic \
      Ai__Defaults__Model=claude-opus-4-8 \
      Ai__Defaults__CheapModel=claude-haiku-4-5

  # Transit engine + key for tenant AI keys / visitor JWT derivation
  vault secrets enable transit || true
  vault write -f transit/keys/dcms-tenant-secrets
'
```

> **`Visitor__SigningKey` is not optional.** It signs every visitor JWT, and
> `VisitorTokenOptions` falls back to a development key that is committed to this
> repository — so an unset key would make every tenant's visitor sessions forgeable
> by anyone who has read this repo. content-api therefore **refuses to start in
> Production** when it is unset, the development default, or shorter than 32 bytes.
> Generate one with `openssl rand -base64 32`.

### Policies, AppRoles and the service credentials

**This section used to create a single `dcms` policy and one shared token for every service,
and called AppRole-per-service "the hardening target". That is done — see
[`setup.md` §2.6](setup.md#26-policies-approles-and-secret-values).**

The short version:

```bash
VAULT_ADDR=... VAULT_TOKEN=<admin> infra/vault/apply.sh
infra/vault/apply.sh --print-role-ids
vault write -f -field=secret_id auth/approle/role/dcms-<service>/secret-id
```

Each service then authenticates as itself and can read only `secret/dcms/shared` and
`secret/dcms/<service>`. A shared token gave every service every secret, including letting the
admin plane decrypt tenant AI keys that only ai-gateway should ever read.

**Which values go at which path is [`vault-secrets.md`](vault-secrets.md).**

> Vault re-seals on every restart under Shamir. Configure Transit auto-unseal against the seal
> Vault — [`seal-vault.md`](seal-vault.md) — or the platform stays down after every reboot
> until a human fetches a key.

### 2.4 DNS

- **Admin/identity plane:** point `admin.example.com` (your `PUBLIC_BASE_URL`
  host) at the server. See step 2.6 for exposing it.
- **Tenant sites:** each tenant custom domain (e.g. `www.acme.com`) gets a DNS
  `A`/`AAAA` record to the server. Caddy issues a TLS cert on first request, but
  **only after** site-host confirms the domain is verified+linked — so verify the
  domain in the admin panel first (TXT record `_dcms-verify.<host>`), then link it
  to a published site.

### 2.4a Vault: policies, AppRoles and auto-unseal

Apply the Vault configuration. Idempotent — run it again after any policy change:

```bash
VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN=<admin> infra/vault/apply.sh
```

That creates the KV and Transit mounts, the two Transit keys, **one policy per service**
(`infra/vault/policies/dcms-<service>.hcl`) and an AppRole bound to each. Secret *values*
are never written by it — a deploy that can rewrite secrets is a deploy that can silently
replace them. Write those once by hand:

```bash
vault kv put secret/dcms/content-api Visitor__SigningKey=...
vault kv put secret/dcms/admin-api   Audit__ChainKey=...
```

Then issue each service its credentials and put them in the host's `.env`:

```bash
infra/vault/apply.sh --print-role-ids
vault write -f -field=secret_id auth/approle/role/dcms-admin-api/secret-id
```

**Why per-service AppRoles.** The previous deployment gave every service the same 72-hour
periodic token with read on `secret/data/dcms/*`. It expired — nothing renews it, and the
failure is invisible until the next restart, so every service fails at once during a deploy
and looks like the deploy broke something. And it had no separation: ai-gateway could read
identity's secrets, site-builder could read the SMTP relay password. Each AppRole is scoped
to `secret/dcms/shared` plus that service's own path. `admin-api` gets Transit **encrypt**
for tenant AI keys and deliberately not decrypt; only `ai-gateway` decrypts, at call time.

A service falls back to `VAULT_TOKEN` when its role/secret pair is unset, so this migrates
one service at a time rather than as a cutover.

**Auto-unseal.** With the default Shamir seal a reboot leaves Vault sealed, every service
refuses to start on the 503, and the platform stays down until a human unseals it — at
whatever hour it happened, with a key they have to go and fetch. Copy
`infra/vault/server/seal-transit.hcl` to `seal-transit.hcl` on the host to point it
at the seal Vault on VPSM. That file documents the trade it makes — VPSM also serves GitLab,
so the seal host is the CI host — and the Shamir→Transit migration, which is not automatic.

`scripts/deploy.sh` checks the seal state before rolling anything, because deploying into a
sealed Vault rolls the whole stack into a crash loop.

### 2.4b Generate the identity signing and encryption certificates

**Required before the application tier will start in Production.** Without them
OpenIddict mints keys into a per-container store: each replica publishes a
different JWKS (so a token minted by one is rejected by the others, pinning
identity to a single replica), and recreating the container invalidates every
live token — a forced logout for every user on every deploy.

```bash
for kind in signing encryption; do
  openssl req -x509 -newkey rsa:2048 -sha256 -days 1825 -nodes \
    -keyout "$kind.key" -out "$kind.crt" -subj "/CN=DCMS Identity ${kind^}"
  openssl pkcs12 -export -out "$kind.pfx" -inkey "$kind.key" -in "$kind.crt" -passout pass:
done

echo "IDENTITY_SIGNING_CERTIFICATE=$(base64 -w0 signing.pfx)"     >> .env
echo "IDENTITY_ENCRYPTION_CERTIFICATE=$(base64 -w0 encryption.pfx)" >> .env
shred -u signing.key encryption.key signing.pfx encryption.pfx
```

Identity **refuses to start** in Production without both. To roll out in stages,
set `IDENTITY_ALLOW_EPHEMERAL_KEYS=true` to accept the old behaviour explicitly —
it is an escape hatch, not a setting to leave on.

Rotating the signing certificate invalidates every token signed with the old one,
so rotate in a maintenance window, or configure the new one alongside the old
before removing the old.

### 2.5 Start the application tier

```bash
$C up -d --build
```

admin-api runs all EF migrations and applies the RLS policies on startup
("All databases migrated." / "Row-Level Security policies applied"). Caddy comes
up on `80`/`443`.

### 2.6 Expose the admin + identity plane (optional but usual)

The prod Caddy proxies **tenant domains → site-host**. To also serve the admin
SPA and identity over TLS on `admin.example.com`, add site blocks to
`infra/caddy/Caddyfile` (these use normal ACME, not on-demand):

```caddyfile
admin.example.com {
    handle /connect/* { reverse_proxy identity:8080 }
    handle /.well-known/* { reverse_proxy identity:8080 }
    handle /api/* { reverse_proxy admin-api:8080 }
    handle { reverse_proxy admin-spa:80 }
}
```

Then set `PUBLIC_BASE_URL=https://admin.example.com`, rebuild the SPA so the
OIDC authority is baked in, and reload Caddy:

```bash
$C up -d --build admin-spa
# NOT `caddy reload`. The Caddyfile is a bind-mounted FILE; replacing it gives
# the container a stale inode, so a reload re-reads the old content and reports
# success. Recreate the container instead.
$C up -d --force-recreate --no-deps caddy
```

### 2.7 Verify production

```bash
curl -fsS https://admin.example.com/health            # via Caddy → admin-api (if exposed)
$C ps
$C logs --since=5m caddy site-host
# Optional load run against the deployed host (k6 runs in a container, nothing to install).
# See loadtest/README.md; raise RATE_LIMIT_PERMITS for the window and put it back after.
./loadtest/run.sh --env vps1 --scenario delivery --vus 10 --duration 2m
```

---

## 3. Day-2 operations

**Deploying new code is a pipeline job, not a command.**

| Trigger | What happens |
|---|---|
| Merge request | build + unit + integration tests. No images, no deploy. |
| Push to `master` | build + test → an image per service (`:<sha>`, `:dev`) → deploy to **development** |
| Tag `v*` | **promote** the images already built for that commit → manual gate → deploy to **production** |

Two properties are worth stating, because both are easy to lose:

- **A release tag never rebuilds.** `promote-images` retags the manifests built
  from that commit (`docker buildx imagetools create`, a registry-side copy that
  cannot change the digest). Production therefore runs the exact bits development
  validated. A rebuild from the same source can differ — a moved base image, a
  republished dependency — and would silently invalidate everything dev proved.
  If no image exists for the tagged commit, the job **fails** rather than building
  one: it means the tag points at a commit that never reached `master`.
- **Deploys are by digest, not tag.** `scripts/ci/resolve-digests.sh` writes
  `docker-compose.images.yml` pinning every service to a `sha256`, and that file is
  kept on the target host. Rollback is then re-applying the previous file, with no
  dependence on where a tag points now.

To deploy by hand — a host that builds from source rather than pulling:

```bash
scripts/deploy.sh --check     # validate the overlay set first; changes nothing
scripts/deploy.sh --build     # build serially, roll, health-gate
```

To roll back:

```bash
scripts/deploy.sh --rollback   # re-applies the previous digest set
```

### What the pipeline needs configured

In **Settings → CI/CD → Variables**:

| Variable | Purpose |
|---|---|
| `SSH_PRIVATE_KEY` | deploy user's key (masked) |
| `SSH_KNOWN_HOSTS` | `ssh-keyscan` output for the targets — pinned, not `StrictHostKeyChecking=no` |
| `DEV_DEPLOY_TARGET` / `PROD_DEPLOY_TARGET` | `user@host` |
| `DEV_DEPLOY_PATH` / `PROD_DEPLOY_PATH` | repo path on the target (default `baas-dcms`) |

Protect the **production** environment so only authorised users can run the manual
deploy job, and note it carries `resource_group: production` — two concurrent
production deploys would race on the host's rollback state.

The target host needs Docker, a `.env` (see `.env.example`) and nothing else. It no
longer needs a source checkout: the previous procedure `tar`-synced `src/`,
`apps/admin/` and `packages/` and built on the host, which meant production built
its own images from a tree that was not a git repository — nothing tied what was
running to a commit, and the registry images CI built went unused.

**Migrations** run as a one-shot job, not at service startup. `scripts/deploy.sh`
runs them before rolling anything, and a failing job stops the deploy with the
running stack untouched:

```bash
$C run --rm nats-init                             # JetStream streams, retired durables
$C run --rm minio-init                            # object-storage buckets and policies
$C --profile migrate run --rm postgres-bootstrap  # schemas, extensions, non-owner roles
$C --profile migrate run --rm migrate             # tables, RLS policies, obs views
$C --profile migrate run --rm identity-migrate    # identity + OpenIddict, seeding
```

`nats-init` and `minio-init` have no profile, so a plain `up -d` starts them too — but
it starts the *existing* exited container and ignores its exit code. Running them here
gives the deploy a fresh container built from the definition that was just rsync'd, and
a failure that stops the deploy instead of surfacing days later as a consumer that
receives nothing. Both scripts are idempotent, so `up -d` re-running them costs nothing.

The order is the schema's own dependency order. `postgres-bootstrap` re-applies
`infra/postgres/init/*` against a running cluster — those scripts are all written
idempotently, but Postgres only executes `docker-entrypoint-initdb.d` on an empty
data directory, so until now every role added since vps1 was provisioned had to be
created by hand against production. `migrate` then creates the tables inside those
schemas, and `identity-migrate` runs last because identity's `DbContext` maps the
audit outbox — seeding a user writes an audit row into a schema the admin-api job
creates.

The production overlay sets `Tenancy__Migrate=false`, `Identity__Migrate=false`
and `Identity__Seed=false` so no service runs DDL. Both jobs take a Postgres
advisory lock, so even if a service is started with its migrate flag back on, two
instances cannot race on `CREATE TABLE` or on `__ef_migrations_history`.

The RLS coverage assertion still runs at *every* service startup. It reads no
database, and it is the guard that fails the boot when a tenant table has been
added without a matching entry in `RlsConfigurator.TenantTables` — an omission
that otherwise silently leaves the table with a grant and no policy.

**Scale the read tier.** content-api is stateless and the Redis backplane
carries chat messages across replicas:

```bash
$C up -d --scale content-api=3
```

**SignalR hubs scale, but only because every client skips negotiation.** The
backplane solves message fan-out, not connection affinity: with negotiation,
`POST /negotiate` and the follow-up transport connect must land on the same
replica and nothing at the edge guarantees that. All three clients — the admin
chat console, the embeddable visitor widget, and the notification hook — set
`skipNegotiation: true` with a WebSockets-only transport, which removes the
requirement. ⚠️ A new hub client that omits it will drop connections at random
once a service is scaled past one replica, and will do so intermittently enough
to look like a network problem.

Note the two hubs live in different services: `/hub/chat` in content-api,
`/api/hub/notifications` in admin-api. Scaling either scales its own hub. Note
also that the rate limiter is in-process, so the effective limit is
`RateLimiting:PermitLimit` × replicas.

**Logs / status:**

```bash
$C logs -f <service>
$C ps
```

**Backups** (persist the named volumes): `postgres-data` (source of truth),
`minio-data` (media + site artifacts), `vault-data` (secrets — back up the unseal
keys/token separately and offline). Example Postgres dump:

```bash
docker compose exec postgres pg_dump -U dcms dcms | gzip > dcms-$(date +%F).sql.gz
```

**Health checks** are wired into Compose (`/health/live`); unhealthy services
restart (`restart: unless-stopped`).

---

## 4. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| App services crash-loop in prod | Vault sealed or not provisioned (step 2.3), or `VAULT_TOKEN` lacks the `dcms` policy. Check `docker compose logs admin-api`. |
| "Refusing to start … dev-mode root token" | `DCMS_REFUSE_DEV_VAULT=true` + a dev token. Use a real Vault token in `.env`. |
| "Refusing to start: `Visitor__SigningKey` …" | The manual Vault step in 2.3 was skipped, or the key is under 32 bytes. |
| "Refusing to start: `Forgejo__WebhookSecret` …" | Empty or a known default. Set `FORGEJO_WEBHOOK_SECRET` and re-register the site webhooks. |
| "Refusing to start: tenant-scoped table(s) … no Row-Level Security policy" | A new tenant table needs an entry in `RlsConfigurator.TenantTables` (or `ExemptTables`, with a reason). |
| SPA login redirect fails | Issuer mismatch — `dcms-identity` hosts entry (dev) or `PUBLIC_BASE_URL` + identity exposed via Caddy (prod). |
| Tenant domain has no cert | Domain not verified+linked yet — site-host's `/internal/tls-allowed` returns 404, so Caddy won't mint a cert. Verify the TXT record and link a published site. |
| `429 Too Many Requests` on content-api | Per-IP rate limit (default 600/60s). Tune `RateLimiting:PermitLimit` / `WindowSeconds`. |
| Build fails pulling packages | Host needs outbound access to NuGet/npm/distro mirrors at build time. |

See `docs/runbook.md` for per-feature configuration and the ADRs in `docs/adr/`
for the architecture decisions referenced above.
