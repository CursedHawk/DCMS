# Deploying baas-dcms on Linux with Docker

This guide deploys the whole system — 7 .NET services, the admin SPA, and the
infra (Postgres, Redis, NATS JetStream, MinIO, Vault, Caddy) — on a single Linux
host using Docker Compose. It covers a **quick start** (dev/staging, all-in-one)
and a **production deployment** (real Vault, Caddy TLS for tenant domains,
secrets from the environment).

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

- **Resources:** the build compiles 7 .NET services + a Node/Vite SPA and runs
  six infra containers. Give the host at least **4 vCPU / 8 GB RAM / 20 GB disk**.
  The site-builder image also bundles Node + ffmpeg-adjacent toolchains.
- **Ports:** dev exposes `5000-5007`, `5432`, `6379`, `4222/8222`, `9000/9001`,
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
curl -fsS localhost:5003/api/_plugins | jq length   # → 12
```

Dev endpoints: admin SPA `:5000`, identity `:5001`, admin-api `:5002`,
content-api `:5003`, media-worker `:5004`, site-builder `:5005`, site-host
`:5006`, ai-gateway `:5007`, Postgres `:5432`, Redis `:6379`, NATS `:4222`
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
EOF
chmod 600 .env
```

Generate strong values with `openssl rand -base64 36`.

### 2.2 Bring up infrastructure first

Start the data/infra tier so you can initialise Vault before the app services
boot (they read config from Vault at startup):

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build \
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

Create a least-privilege policy + token for the services and put that token in
`.env` as `VAULT_TOKEN` (AppRole-per-service is the hardening target; a single
scoped token is the simplest correct start):

```bash
docker compose exec vault sh -c '
  cat > /tmp/dcms.hcl <<POLICY
path "secret/data/dcms/*" { capabilities = ["read"] }
path "transit/encrypt/dcms-tenant-secrets" { capabilities = ["update"] }
path "transit/decrypt/dcms-tenant-secrets" { capabilities = ["update"] }
POLICY
  vault policy write dcms /tmp/dcms.hcl
  vault token create -policy=dcms -period=72h
'
# Copy the token into .env → VAULT_TOKEN, then it is read by the services.
```

> Vault re-seals on every restart. For unattended reboots, configure auto-unseal
> (cloud KMS / transit) — manual unseal is fine for a single managed host.

### 2.4 DNS

- **Admin/identity plane:** point `admin.example.com` (your `PUBLIC_BASE_URL`
  host) at the server. See step 2.6 for exposing it.
- **Tenant sites:** each tenant custom domain (e.g. `www.acme.com`) gets a DNS
  `A`/`AAAA` record to the server. Caddy issues a TLS cert on first request, but
  **only after** site-host confirms the domain is verified+linked — so verify the
  domain in the admin panel first (TXT record `_dcms-verify.<host>`), then link it
  to a published site.

### 2.5 Start the application tier

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
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
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build admin-spa
docker compose -f docker-compose.yml -f docker-compose.prod.yml exec caddy caddy reload --config /etc/caddy/Caddyfile
```

### 2.7 Verify production

```bash
curl -fsS https://admin.example.com/health            # via Caddy → admin-api (if exposed)
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
docker compose -f docker-compose.yml -f docker-compose.prod.yml logs --since=5m caddy site-host
# Optional load smoke against content-api (install k6 on a client):
k6 run -e BASE=https://<a-tenant-domain> -e TENANT=<slug> -e SLUG=<instance> scripts/load-smoke.js
```

---

## 3. Day-2 operations

**Migrations** run automatically on admin-api startup (gated by `Tenancy:Migrate`,
default true). To deploy new code:

```bash
git pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

**Scale the read tier** (content-api is stateless; chat uses the Redis backplane
so it crosses replicas):

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --scale content-api=3
```

**Logs / status:**

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml logs -f <service>
docker compose -f docker-compose.yml -f docker-compose.prod.yml ps
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
| SPA login redirect fails | Issuer mismatch — `dcms-identity` hosts entry (dev) or `PUBLIC_BASE_URL` + identity exposed via Caddy (prod). |
| Tenant domain has no cert | Domain not verified+linked yet — site-host's `/internal/tls-allowed` returns 404, so Caddy won't mint a cert. Verify the TXT record and link a published site. |
| `429 Too Many Requests` on content-api | Per-IP rate limit (default 600/60s). Tune `RateLimiting:PermitLimit` / `WindowSeconds`. |
| Build fails pulling packages | Host needs outbound access to NuGet/npm/distro mirrors at build time. |

See `docs/runbook.md` for per-feature configuration and the ADRs in `docs/adr/`
for the architecture decisions referenced above.
