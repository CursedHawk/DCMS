# Migrating vps1 from hand-run deploys to the pipeline

One-time procedure. vps1 becomes the **dev** environment, deployed by a GitLab pipeline on
every push to `master`, instead of by scripts typed on the host.

## What actually changes

| | Before | After |
|---|---|---|
| Source on the host | full tree, `tar`-synced, not a git repo | compose files + `infra/` + `scripts/` only |
| Images | built **on vps1** from that tree | built by a runner, pulled by **digest** from the registry |
| Trigger | a human runs `run-deploy-full.sh` | push to `master` |
| Migrations | at service startup | one-shot jobs, before the roll |
| Rollback | none | `scripts/deploy.sh --rollback` (previous digest set) |
| Registry credential on the host | none (never pulled) | none — CI logs the host in for the deploy and out again |

**Public hostnames do not change.** vps1 keeps `admin.highgeek.eu`, `grafana.highgeek.eu`
and `git.highgeek.eu` for now. The `*.dev.highgeek.eu` split belongs to the production
cutover (Phase F): moving the names before the production host exists would point
`*.highgeek.eu` at nothing. The hostnames are variables now, so the split is an `.env` edit
and a DNS change when the time comes — see `ADMIN_HOST` / `GRAFANA_DOMAIN` / `GIT_HOST`.

## Already done

- **`.env` on vps1 extended to the full 60-variable contract** (was 28), backed up first to
  `.env.bak-pipeline-migration-*`. Identity's signing and encryption certificates were
  generated **on vps1** so the private keys never left it, and `RLS_DB_PASSWORD` was
  generated — until now `dcms_rls`, which holds SELECT on every tenant schema, was live on
  a password published in this repository.
- **`DCMS_ENV=dev` / `DCMS_HOST=vps1`** set. dev and prod resolve to the *same* three-file
  overlay set from the *same* config files, so these two labels are the only thing that
  distinguishes their telemetry.
- **Deploy key installed** in vps1's `authorized_keys`, with agent/port/X11 forwarding
  disabled. Verified working.
- **Preflight passed** against vps1's real `.env`, from a scratch copy of the new artifact in
  `~/baas-dcms-next`, and the rendered configuration diffed against what is running. The only
  differences are the intended ones: migration flags off, certificates present, the SPA's
  authority injected at runtime rather than baked in, and the new host/env labels.

## Left to do

### 1. Four CI/CD variables

**Settings → CI/CD → Variables.** Mark all four **Protected** (`master` is a protected
branch, and the deploy job only ever runs there).

| Key | Type | Value |
|---|---|---|
| `SSH_PRIVATE_KEY` | **File** | contents of `~/.ssh/dcms-ci-deploy` on VPSM |
| `SSH_KNOWN_HOSTS` | **File** | `ssh-keyscan -t rsa,ecdsa,ed25519 57.128.239.132` |
| `DEV_DEPLOY_TARGET` | Variable | `cursedhawk@57.128.239.132` |
| `DEV_DEPLOY_PATH` | Variable | `baas-dcms` |

`SSH_PRIVATE_KEY` must be **File** type: a private key is multi-line and GitLab cannot mask
a multi-line value, so a plain variable would sit unmasked in the settings UI. The job
handles either form.

After setting them, delete `~/.ssh/dcms-ci-deploy` from VPSM — GitLab is then the only
place it exists, and regenerating it is a two-minute job if it is ever lost.

There is deliberately **no registry credential** here. The deploy job logs the target in
with its own `$CI_JOB_TOKEN` and logs it out in a trap when the job ends, so a stolen vps1
disk yields nothing that can pull tomorrow's images.

### 2. Push to `master`

The first pipeline builds all ten images (~15 min, no warm cache), pushes them as
`:<short-sha>` and `:dev`, then deploys.

### 3. Vault must be recreated once, attended

The Vault container's config mount changes from a single file to a directory
(`infra/vault/server/`), which is what lets a host enable Transit auto-unseal by dropping in
one file. vps1's Vault is **Shamir-sealed**, so recreating it seals it — and every service
reads its configuration from Vault at startup and will not boot.

`scripts/deploy.sh` therefore **skips** recreating a Shamir-sealed Vault and says so. Do it
by hand, once, when you are watching:

```bash
cd ~/baas-dcms
C="docker compose -f docker-compose.yml -f docker-compose.prod.yml -f docker-compose.vps.yml"
$C up -d --force-recreate --no-deps vault
./unseal-vault.sh
```

This stops being a manual step once Transit auto-unseal is configured against the seal Vault
on VPSM.

### 4. Retire the old scripts

After the first successful pipeline deploy:

```bash
mkdir -p ~/baas-dcms/retired-deploy-scripts
mv ~/baas-dcms/run-deploy*.sh ~/baas-dcms/retired-deploy-scripts/
```

They are a live trap while they remain: `~/baas-dcms`'s source tree is frozen at the last
`tar` sync and is no longer updated, so running one would rebuild the stack from stale
source and overwrite the images the pipeline just deployed. Keep `unseal-vault.sh`,
`vault-token-renew.sh` and `dcms-token.sh` — those are operations, not deploys.

Also remove the scratch preflight copy once you are satisfied: `rm -rf ~/baas-dcms-next`.

## Verifying the cutover

```bash
# images now come from the registry, by digest, not from a local build
ssh vps1 'docker inspect dcms-admin-api-1 --format "{{.Image}} {{index .Config.Labels \"org.opencontainers.image.revision\"}}"'
ssh vps1 'cd ~/baas-dcms && ls .deploy/'          # current + previous digest sets
```

- Log in at `https://admin.highgeek.eu` — tokens are now signed by a persisted certificate,
  so this session survives the next deploy rather than being invalidated by it.
- Grafana at `https://grafana.highgeek.eu` — new series carry `environment="dev"`,
  `host="vps1"`.

## Rolling back

```bash
ssh vps1 'cd ~/baas-dcms && scripts/deploy.sh --env dev --rollback'
```

That re-applies the previous digest set recorded in `.deploy/`, which is an exact release
rather than wherever a tag points now. If the host's state is gone, every pipeline keeps its
`docker-compose.images.yml` as an artifact for 90 days.

To abandon the migration entirely, the old world is intact: `.env.bak-pipeline-migration-*`
and the untouched source tree in `~/baas-dcms`.
