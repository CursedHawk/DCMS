# Setting up DCMS from nothing

The whole platform, host by host: a GitLab that builds images, a Vault that unseals the other
Vaults, and one or two serving hosts that deploy themselves from the registry.

Read [`architecture`](#what-you-are-building) first — several steps only make sense once you
know which host owns what.

Deep detail lives in companion documents and is not repeated here:

| | |
|---|---|
| Which secret goes where | [`vault-secrets.md`](vault-secrets.md) |
| The seal Vault | [`seal-vault.md`](seal-vault.md) |
| Single-host bring-up in detail | [`deploy-linux.md`](deploy-linux.md) |
| Observability stack | [`../infra/observability/README.md`](../infra/observability/README.md) |
| Day-2 operations | [`runbook.md`](runbook.md) |

---

## What you are building

| Host | Role | Runs | Deploys when |
|---|---|---|---|
| **ops** | build + trust root | GitLab, its container registry, the **seal Vault** | — |
| **dev** | development | the full DCMS stack | every push to `master` |
| **prod** | production | the full DCMS stack | every `v*` tag, behind a manual gate |

**dev and prod are completely separate.** Each has its own Postgres, Vault, MinIO, NATS, Redis
and its own observability stack. They share exactly one thing: the seal Vault on the ops host
unwraps each one's master key. It holds no application data.

Both serving hosts run the **same three compose files and the same image digests**. The only
things that differ are the host's `.env` and which Transit key unseals it. That is deliberate:
if dev and prod could drift in their deployment description, dev would stop being evidence
about prod.

**GitLab Runners run elsewhere.** They need Docker-in-Docker for `buildx` and SSH reachability
to the serving hosts. Nothing else on any of the three hosts above.

---

## Prerequisites

On every host:

```bash
# Docker Engine + Compose v2
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker "$USER"     # log out and back in
docker compose version              # must print v2.x
```

- A private network between the hosts. Tailscale is what this deployment uses; anything that
  gives stable addresses and encrypted transport works. **The seal Vault must be reachable
  only over it.**
- DNS you control, with the ability to point a wildcard at a serving host.
- Ports 80 and 443 open on each serving host. Nothing else needs to be public.

Sizing: a serving host runs ~25 containers, of which the observability stack reserves about
2.2 GB. 4 vCPU / 8 GB works for dev and has no headroom to spare — see the
[trap about building on a serving host](#traps-that-have-actually-bitten).

---

## Part 1 — The ops host

### 1.1 GitLab and its registry

Any GitLab that can run CI and host a container registry. The registry must be reachable from
both serving hosts, since that is where images are pulled from.

```bash
# in /etc/gitlab/gitlab.rb
registry['enable'] = true
registry_external_url 'https://registry-gitlab.example.com'
```

Give the container a memory limit with room to work. Ten concurrent CI jobs streaming logs
while pushing images will use more than you expect — see the traps section.

### 1.2 The seal Vault

A second, tiny Vault whose only job is to hold two Transit keys. Full detail in
[`seal-vault.md`](seal-vault.md); the shape is:

```hcl
# listener bound to the PRIVATE address only
listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = false
  tls_cert_file = "/vault/userconfig/tls/vault.crt"
  tls_key_file  = "/vault/userconfig/tls/vault.key"
}
storage "raft" { node_id = "vault-1", path = "/vault/data" }

# Raft refuses to start without these, and they must name THIS host.
api_addr     = "https://<private-ip>:8200"
cluster_addr = "https://<private-ip>:8201"

# NOT the Vault default of 2h/20m. A seal token is renewed by the target Vault, so if a
# serving host is off for longer than this, its token expires and it can never auto-unseal
# again -- the exact outage auto-unseal exists to prevent.
max_lease_ttl     = "768h"
default_lease_ttl = "768h"
```

Publish the port on the private address only: `"<private-ip>:8200:8200"`, never `0.0.0.0`.

Then initialise, enable Transit, and create one key and one policy per environment:

```bash
vault operator init -key-shares=1 -key-threshold=1   # store the output offline
vault operator unseal <key>

vault secrets enable transit
for env in dev prod; do
  vault write -f transit/keys/dcms-unseal-$env
  vault policy write dcms-unseal-$env - <<EOF
path "transit/encrypt/dcms-unseal-$env" { capabilities = ["update"] }
path "transit/decrypt/dcms-unseal-$env" { capabilities = ["update"] }
EOF
  vault token create -policy=dcms-unseal-$env -period=768h -orphan -field=token
done
```

The policy is the whole policy: wrap and unwrap with one key. It cannot read the key material,
delete it, rotate it, or see the other environment's key. **Orphan** so that revoking the root
token — which you should do — does not take the seal tokens with it.

### 1.3 Unseal the seal Vault at boot

The chain terminates here, so this Vault must open itself or nothing downstream can.

Install `vault-seal-unseal.service` plus a **timer**. The service alone is not enough: the
container carries `restart: unless-stopped`, so a crash or OOM kill brings it back *sealed*
long after boot with nothing to open it.

```ini
# /etc/systemd/system/vault-seal-unseal.timer
[Timer]
OnCalendar=*:0/5
Persistent=true
Unit=vault-seal-unseal.service
```

Use `OnCalendar`, not `OnUnitActiveSec` — see the traps section for why.

Verify it for real, by sealing it and walking away:

```bash
docker restart vault          # comes back sealed
sleep 300
vault status                  # must read Sealed: false with no human action
```

---

## Part 2 — A serving host

Identical for dev and prod. Everything that differs is in `.env`.

### 2.1 Data directories

```bash
mkdir -p ~/dcms-data/{grafana,prometheus,loki,tempo,alloy,build-work}
```

**Do not chown these.** They are named volumes with `o: bind`, and Docker populates an empty
named volume from the image — ownership included — so each store ends up owned by the uid that
needs to write it. Pinning them to your own uid is what a first attempt does, and it produces
`permission denied` on `/prometheus/queries.active`. Loki is the exception: its image ships
`/loki` owned by root while the process runs as 10001, so that one needs a correction the
deploy user cannot make.

### 2.2 The environment file

```bash
cp .env.example .env && chmod 600 .env
```

`.env.example` is the contract and is kept exactly in step with the compose files — every
`${VAR}` in any of the three, and nothing else. Fill in every value; generate secrets with
`openssl rand -base64 36`.

Four that decide what this host *is*:

```bash
DCMS_ENV=dev                              # dev | prod. Also picks the overlay set.
DCMS_HOST=vps1                            # short name; both become Prometheus labels
PUBLIC_BASE_URL=https://admin.dev.example.com
ADMIN_HOST=admin.dev.example.com          # PUBLIC_BASE_URL without the scheme
GRAFANA_DOMAIN=grafana.dev.example.com
GRAFANA_ROOT_URL=https://grafana.dev.example.com/
GIT_HOST=git.dev.example.com
```

`ADMIN_HOST` exists because the edge's route table keys on a hostname while an OIDC issuer is a URL —
one fact needed in two shapes. `scripts/deploy.sh` refuses to deploy when they disagree,
because an edge serving one name while identity issues tokens for another produces a login
loop rather than an error anyone can read.

`DCMS_ENV` and `DCMS_HOST` become Prometheus `external_labels`. Leaving them unset labels the
series `unknown`, which is visible; setting prod's to `dev` produces alerts naming the wrong
host that look entirely plausible. `deploy.sh` **fails** on a mismatch and only warns on unset,
for that reason.

### 2.3 Identity certificates

Required before the application tier will start in Production.

```bash
for kind in signing encryption; do
  openssl req -x509 -newkey rsa:2048 -sha256 -days 1825 -nodes \
    -keyout "$kind.key" -out "$kind.crt" -subj "/CN=DCMS Identity ${kind}"
  openssl pkcs12 -export -out "$kind.pfx" -inkey "$kind.key" -in "$kind.crt" -passout pass:
done
echo "IDENTITY_SIGNING_CERTIFICATE=$(base64 -w0 signing.pfx)"       >> .env
echo "IDENTITY_ENCRYPTION_CERTIFICATE=$(base64 -w0 encryption.pfx)" >> .env
shred -u signing.key encryption.key signing.pfx encryption.pfx
```

Generate them **on the host**, so the private keys never travel. Without them OpenIddict mints
keys into a per-container store: every replica publishes a different JWKS, and recreating the
container invalidates every live token — a forced logout for every user on every deploy.

### 2.4 Bring up infrastructure

```bash
C="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
$C up -d postgres redis nats minio vault
```

**Always the full three-file set.** A partial `-f` list has already caused a production outage
here: the missing overlay silently drops volumes and environment, and the stack comes up
looking healthy against the wrong storage.

### 2.5 This environment's Vault

Initialise and unseal it, then point it at the seal Vault:

```bash
$C exec vault vault operator init -key-shares=1 -key-threshold=1   # store offline
$C exec vault vault operator unseal <key>
```

`infra/vault/server/seal-transit.hcl` is committed and already correct for both environments.
Set the two values that differ, in `.env`:

```bash
VAULT_TRANSIT_SEAL_TOKEN=<the env's token from step 1.2>
VAULT_TRANSIT_SEAL_KEY_NAME=dcms-unseal-dev      # or dcms-unseal-prod
```

Then migrate the seal — **this is not automatic**:

```bash
$C up -d --force-recreate --no-deps vault
$C exec vault vault operator unseal -migrate <shamir key>   # threshold times
$C exec vault vault status | grep -E "Seal Type|Sealed"     # transit / false
```

After migration the Shamir keys become **recovery** keys. Keep them, off both hosts.

### 2.6 Policies, AppRoles and secret values

```bash
VAULT_ADDR=... VAULT_TOKEN=<admin> infra/vault/apply.sh
```

Idempotent: mounts, both Transit keys, one policy per service, one AppRole per service. It
never writes a secret *value* — a deploy that can rewrite secrets is a deploy that can silently
replace them.

Issue each service its credentials into `.env`:

```bash
infra/vault/apply.sh --print-role-ids
vault write -f -field=secret_id auth/approle/role/dcms-<service>/secret-id
# -> VAULT_ROLE_ID_<SERVICE> / VAULT_SECRET_ID_<SERVICE>
```

Then write the secret values themselves — **which ones, and where, is
[`vault-secrets.md`](vault-secrets.md)**. At minimum, content-api will not start without
`Visitor__SigningKey`.

A service with both halves of its AppRole uses it; without them it falls back to `VAULT_TOKEN`.
That is what lets you migrate one service at a time. Once all of them are on AppRole, revoke
the shared token and blank `VAULT_TOKEN` — while it exists, every service still holds a
credential that can read every secret.

Confirm, rather than assume, that they actually switched:

```bash
vault list -format=json auth/token/accessors    # look up each: display_name should be "approle"
```

`healthy` looks identical whether a service used its AppRole or quietly fell back.

---

## Part 3 — The pipeline

### 3.1 Deploy credentials

```bash
ssh-keygen -t ed25519 -N "" -f ~/.ssh/dcms-ci-deploy -C "gitlab-ci deploy -> dcms hosts"
# append the .pub to the deploy user's authorized_keys on each serving host, with
# no-agent-forwarding,no-port-forwarding,no-X11-forwarding
```

Then in **Settings → CI/CD → Variables**, all **Protected** (the deploy jobs only run on
protected refs):

| Key | Type | Value |
|---|---|---|
| `SSH_PRIVATE_KEY` | **File** | the private key |
| `SSH_KNOWN_HOSTS` | **File** | `ssh-keyscan -t rsa,ecdsa,ed25519 <host>` |
| `DEV_DEPLOY_TARGET` / `PROD_DEPLOY_TARGET` | Variable | `user@host` |
| `DEV_DEPLOY_PATH` / `PROD_DEPLOY_PATH` | Variable | `baas-dcms` |

**File** type for both key and known-hosts: GitLab cannot mask a multi-line value, so a plain
variable would sit unmasked in the settings UI.

There is deliberately **no registry credential**. The deploy job logs the target in with its
own `$CI_JOB_TOKEN` and logs it out in a trap when the job ends, so a stolen host disk yields
nothing that can pull tomorrow's images.

### 3.2 Protect the refs

Protect `master` and the `v*` tag pattern, or protected variables will not be exposed to the
deploy jobs and prod will fail at the last step.

### 3.3 What runs when

```
merge request   build + test only
push to master  build + test -> 10 images (:<sha>, :dev) -> deploy dev
tag v*          PROMOTE those digests -> manual gate -> deploy prod
```

A tag **never rebuilds**. `promote-images` retags the manifests built from that commit with
`docker buildx imagetools create`, a registry-side copy that cannot change the digest. If no
image exists for the tagged commit, the job fails — the tag points at a commit that never
reached `master`, which is exactly the case to refuse rather than paper over by building
something new.

Deploys are by **digest**, not tag. `scripts/ci/resolve-digests.sh` writes an overlay pinning
every service to a `sha256`, and it is kept on the host so `scripts/deploy.sh --rollback`
re-applies an exact previous release rather than trusting where a tag points now.

---

## Part 4 — DNS and TLS

| Name | Points at | Certificate |
|---|---|---|
| `admin.<env>.example.com` | serving host | normal ACME |
| `grafana.<env>.example.com` | serving host | normal ACME |
| `git.<env>.example.com` | serving host | normal ACME |
| tenant custom domains | serving host | **on-demand**, gated |

Tenant domains are gated: site-host answers `GET /internal/tls-allowed?domain=…` with 200
only for verified, linked domains, so the edge never orders a certificate for a hostname no
tenant owns. Its sibling `GET /internal/tls-hostnames` returns the whole list, which is what
the hourly sweep uses to issue the certificates it does not already hold — the platform's own
names included, since those bypass the tenant gate.

Grafana must be its **own name**, not something under a wildcard that already resolves —
otherwise it falls through to the catch-all and is correctly refused a certificate.

---

## Part 5 — Verify

```bash
scripts/deploy.sh --env dev --check     # resolves and validates; changes nothing
docker compose $C ps                    # every app service healthy
scripts/obs-smoke.sh                    # observability end to end
```

Then the things that only fail later:

- **Log in.** Tokens are signed by the persisted certificate, so the session must survive the
  next deploy rather than being invalidated by it.
- **Restart Vault.** It must come back `Sealed: false` with no human action.
- **Reboot the host.** Everything must return unattended.
- **Check the labels.** New Prometheus series should carry your `environment` and `host`, not
  `unknown` and not the other environment's name.

---

## Traps that have actually bitten

Each of these has cost an outage or hours of misdirected debugging here.

**Partial `-f` sets.** Always the full three files. A missing overlay drops volumes and
environment silently, and the stack comes up healthy against the wrong storage.

**Building more than two services at once on a serving host.** Thrashes a 4-core box into an
outage. The pipeline builds elsewhere, one image at a time — the `resource_group` on
`build-image` is not a preference.

**Ten concurrent image pushes into a co-located registry.** Same lesson on the ops host: the
builds run on the runners but the *pushes* all land on the machine that also serves GitLab.
That drove load past 50, pinned GitLab against its memory limit, made the web UI 504, and
failed seven of ten jobs.

**Reloading a container whose config is a bind-mounted *file*** (Alloy, Prometheus, the
NATS server). Replacing the file leaves the container on a stale inode, so the reload re-reads
the old content and reports success. Recreate the container instead — which is why the edge's
route table is code and database rows rather than a file.

**Recreating a Shamir-sealed Vault.** It comes back sealed, and every service reads its
configuration from Vault at startup. `deploy.sh` refuses this and explains the attended
procedure; under Transit there is nothing to refuse.

**`vault kv put` on a path that already has keys.** It replaces the whole secret. Use
`kv patch` to add one key, or silently delete the rest.

**Transit seal environment variables are asymmetric.** `key_name` and `mount_path` honour
`VAULT_TRANSIT_SEAL_*`; `address` and `token` do **not** — they fall back to `VAULT_ADDR` and
`VAULT_TOKEN`, and the prefixed forms are ignored without a word. A wrong token sends *no*
token and returns `403 permission denied`, which reads as a policy failure and is not one.

**`OnUnitActiveSec` on a `oneshot` timer.** If the unit ever ran with `RemainAfterExit=yes`, it
stays `active` forever and systemd will not re-trigger it — every later elapse is a silent
no-op behind a timer that looks healthy. Use `OnCalendar`.

**`postgres` init scripts only run on an empty data directory.** Anything added to
`infra/postgres/init/` after provisioning never reaches an existing cluster. The
`postgres-bootstrap` job re-applies them on every deploy for exactly this reason.
