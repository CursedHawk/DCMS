# The seal Vault (VPSM)

The Vault that unwraps the other Vaults' master keys, so dev and prod come back from a reboot
without a human. It holds no application data — only two Transit keys.

Runs from `/main/compose/vault/docker-compose.yml` on VPSM, **not** from this repo: it has to
be able to start before, and independently of, anything this repo deploys.

| | |
|---|---|
| Address | `https://100.70.75.26:8200` — VPSM's Tailscale IP, reachable from vps1 (~23 ms) |
| Storage | raft, single node (`vault-1`), `/main/appdata/vault/data` |
| Seal | Shamir, **1 share / threshold 1** — this is the end of the chain |
| TLS | self-signed CA + server cert, SANs `IP:100.70.75.26, IP:127.0.0.1, DNS:vps-main, DNS:localhost`, **expires 2036-08-25** |
| Transit keys | `dcms-unseal-dev`, `dcms-unseal-prod` |

**It is not published on the public interface.** The compose file binds the port to the
Tailscale address specifically (`100.70.75.26:8200:8200`), not `0.0.0.0`. Keep it that way:
VPSM also serves GitLab on 80/443, and this is the one service on that box that must never be
reachable from the internet.

## What had to be fixed to make it start

Recorded because each one is a failure with a misleading symptom:

- **`cluster_addr` / `api_addr` were commented out.** Raft refuses to initialise without a
  cluster address ("Cluster address must be set when using raft storage"), and the previous
  values pointed at `vault.highgeek.eu`, which is not this host.
- **The data directories were root-owned** while the container runs as uid 100. The error is
  `failed to open bolt file: /vault/data/vault.db: permission denied` — which reads like a
  corrupt volume rather than a chown.
- **The TLS material did not exist.** `userconfig/tls/` was empty while the listener had
  `tls_disable = false`, so Vault would have refused to listen at all.
- **`max_lease_ttl = "2h"` silently capped the seal tokens.** Asking for `-period=768h`
  produced a 2 h token. That is the sharpest edge here: the seal token is renewed by the
  *target* Vault, so if vps1 (or prod) is off for more than the TTL, the token expires and
  that host can never auto-unseal again — the exact outage auto-unseal exists to prevent,
  arriving months later and looking like something else. Raised to 768 h. A seal token must
  outlive the target being offline.

## Least privilege

One policy per environment, and they are the whole policy:

```hcl
path "transit/encrypt/dcms-unseal-<env>" { capabilities = ["update"] }
path "transit/decrypt/dcms-unseal-<env>" { capabilities = ["update"] }
```

No read of the key material, no delete, no rotate, and no sight of the other environment's
key. Verified from vps1: the dev token encrypts and decrypts with `dcms-unseal-dev` and gets
`permission denied` on `dcms-unseal-prod`.

Both tokens are **periodic and orphaned** — orphaned so that revoking the root token does not
take the seal tokens with it, and periodic because a seal token that expires means that
environment's Vault never comes back from its next restart.

### The operator role

`infra/vault/seal/seal-ops.hcl`, attached to the `seal-ops` AppRole alongside both unseal
policies. It exists so nothing routine needs the root token: it can create and inspect the
per-environment transit keys, write the `dcms-unseal-*` policies, and mint and revoke seal
tokens for an environment.

It cannot seal this Vault (which would take dev and production down together), delete a
transit key (which would permanently brick that environment's Vault — the master key is
wrapped with it and nothing else can unwrap), touch the audit device, generate a root token,
or rewrite its own policy.

It does carry `sudo` on `auth/token/create`, for one reason: Vault requires root or sudo to
create a **periodic** token, and a seal token must be periodic. `sudo` lifts that restriction
and nothing else — the subset rule still holds, so this token cannot create a child with a
policy it does not itself have.

```bash
set -a; . ~/.dcms/vault-seal-ops.env; set +a
T=$(docker exec vault vault write -field=token auth/approle/login \
      role_id=$VAULT_SEAL_OPS_ROLE_ID secret_id=$VAULT_SEAL_OPS_SECRET_ID)
docker exec -e VAULT_TOKEN="$T" vault vault token create \
      -policy=dcms-unseal-prod -period=768h
```

## Secrets, and where they are

On VPSM, 0600 inside a 0700 `~/.dcms` — **not** in this repo, not in any image, and not in
`~/baas-dcms`, which CI rsyncs into and which is a git worktree:

| File | Holds |
|---|---|
| `~/.dcms/vault-seal-ops.env` | the `seal-ops` AppRole. The credential for everyday use |
| `~/.dcms/vault-seal-recovery.json` | the seal Vault's unseal keys, recovery keys **and** root token |
| `~/.dcms/README` | what to take offline, and the root-token decision below |
| `/etc/vault-seal/unseal.key` | root-only copy of the unseal key, read by the systemd timer |

### Why the root token is still live

It should be revoked, and on most Vault deployments it would be: revoke it, and regenerate one
from the recovery keys when it is next needed. **That does not work here.** On Vault 2.0.4
`sys/generate-root/attempt` returns 403 without a root token — verified on both Vaults, with
an ops token, with an invalid token, and with no token header at all. The recovery keys cannot
mint a replacement, so revoking root is a one-way door leaving only the ops AppRole, which by
design cannot rewrite its own policy or manage audit devices.

That is a decision to take deliberately rather than a step to perform quietly. The exposure
has been reduced instead: root is out of the synced deploy directory, it is 0600 in a 0700
directory, and nothing routine uses it.

## Still to do

1. ~~Unseal the seal Vault at boot.~~ **Done.** `vault-seal-unseal.service` opens it after
   `docker.service`, and `vault-seal-unseal.timer` re-checks every five minutes — because the
   container carries `restart: unless-stopped`, so a crash or OOM kill brings it back *sealed*
   long after boot, with nothing to open it. The script exits immediately when already
   unsealed. Key at `/etc/vault-seal/unseal.key`, root-only.

   Two things that bit during setup and would bite again: `vault operator unseal -` (key on
   stdin) is **not** supported by this Vault, and a `oneshot` unit left with
   `RemainAfterExit=yes` stays `active` forever, which makes every later timer elapse a silent
   no-op.
2. ~~Migrate vps1 from Shamir to Transit.~~ **Done.** vps1 reports
   `Seal Type: transit`, `Recovery Seal Type: shamir`, and comes back **unsealed** from a
   container restart with no human action. Its Shamir key is now a *recovery* key.

   The migration cost two hours to a single undocumented asymmetry, recorded in
   `infra/vault/server/seal-transit.hcl`: the transit seal's `address` and `token` are read
   from the standard `VAULT_ADDR` / `VAULT_TOKEN`, **not** from `VAULT_TRANSIT_SEAL_ADDRESS` /
   `VAULT_TRANSIT_SEAL_TOKEN`, which are ignored. Only `key_name` and `mount_path` honour the
   prefixed form. A wrong token is the nastier of the two: the request arrives with *no* token
   and returns 403 "permission denied", which reads as a policy failure and is not one. The
   seal Vault's audit log settled it — `"policies": null` on the denied request.

**An audit device is enabled** on the seal Vault at `/vault/audit/audit.log`. It is what
diagnosed the above, and it is the only record of use for the box's most sensitive component.
Note the interaction with disk: Vault **fails requests when it cannot write an audit log**, so
if VPSM's disk fills, the seal Vault stops answering and nothing downstream can unseal. The
volume is a few requests per unseal, so this is a reason to keep the disk healthy rather than
to disable the audit.

## The trade this makes

Documented in `infra/vault/server/seal-transit.hcl`, and worth restating in one line:
root on VPSM already meant the ability to ship arbitrary code to prod through GitLab, and this
widens that to unwrapping both environments' Vaults.

**It also introduces an availability coupling that did not exist before.** vps1's Vault could
previously be unsealed with a key on vps1 itself; once it seals with Transit, it cannot start
without VPSM reachable. VPSM ran out of memory and stopped serving earlier today, so this is
not a theoretical concern — weigh it before migrating production.
