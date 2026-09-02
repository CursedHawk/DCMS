#!/usr/bin/env bash
#
# Applies DCMS's Vault configuration: mounts, transit keys, one policy per service, and an
# AppRole per service bound to that policy. Idempotent — safe to run on every deploy.
#
# Deliberately the `vault` CLI and not Terraform. Terraform would need state, and the state
# for a secrets manager is itself sensitive and has to live somewhere with its own backup and
# locking story. Everything here is convergent, so re-running is the whole mechanism.
#
# Secret VALUES are never written by this script. Those are entered by an operator, once, and
# the pipeline only asserts they exist (see --check). A deploy that could rewrite secrets is a
# deploy that can silently replace them.
#
# Usage:
#   VAULT_ADDR=... VAULT_TOKEN=<admin token> infra/vault/apply.sh [--check] [--print-role-ids]
#
#   --check           Assert required paths and keys exist. Changes nothing. Exit 1 if not.
#   --print-role-ids  Print each service's role_id (not secret). For provisioning a node.
#
# No host needs the vault binary installed. Every policy is piped on stdin rather than passed
# as a filename, so `vault` can be a one-line shim that execs into the running container:
#
#   PATH="$(pwd)/infra/vault/bin:$PATH" infra/vault/apply.sh
#
# See infra/vault/bin/vault. Passing filenames would not work through such a shim -- the path
# would be resolved inside the container, where the policies directory is not mounted.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

SERVICES="identity admin-api content-api media-worker site-builder site-host ai-gateway email-worker"

# Keys that must exist in secret/dcms/shared for any deployment to work. Presence only --
# this never reads a value.
REQUIRED_SHARED=""

# Per-service required keys. A missing one here is a service that will boot and then fail in a
# way that looks unrelated, which is exactly what the assertion exists to prevent.
#
# This list grew as values moved out of .env, and it has to keep tracking them: the compose
# overlays no longer carry any of these, so a path missing a key is now a service that does
# not start rather than one that quietly uses a stale environment value. Asserting here means
# the deploy stops before anything rolls, with the path and key named.
#
# Deliberately NOT listed, and each for a reason:
#   Authentication__Google__*   optional -- absent simply hides the sign-in button
#   Email__User/__Password      a relay on a private network may need no auth
#   Alerting__WebhookSecret     shared with the Grafana container, so it stays in .env
required_keys_for() {
  case "$1" in
    identity)     echo "Audit__ChainKey Identity__AdminApiService__Secret Identity__SuperAdmin__Password Identity__SigningCertificate Identity__EncryptionCertificate" ;;
    admin-api)    echo "Audit__ChainKey ServiceClient__ClientSecret Forgejo__Token Forgejo__AdminToken Forgejo__WebhookSecret" ;;
    content-api)  echo "Audit__ChainKey ServiceClient__ClientSecret Visitor__SigningKey" ;;
    media-worker) echo "Audit__ChainKey" ;;
    site-host)    echo "Audit__ChainKey" ;;
    ai-gateway)   echo "Audit__ChainKey Ai__Defaults__Provider Ai__Defaults__Model" ;;
    email-worker) echo "Email__Host Email__Port Email__FromAddress" ;;
    *)            echo "" ;;
  esac
}

MODE="apply"
for arg in "$@"; do
  case "$arg" in
    --check)          MODE="check" ;;
    --print-role-ids) MODE="role-ids" ;;
    *) echo "apply.sh: unknown argument $arg" >&2; exit 2 ;;
  esac
done

command -v vault >/dev/null || { echo "apply.sh: the vault CLI is required" >&2; exit 2; }
: "${VAULT_ADDR:?VAULT_ADDR must be set}"

# ---------------------------------------------------------------------------
# check: assert, change nothing
# ---------------------------------------------------------------------------

if [ "$MODE" = "check" ]; then
  missing=0

  for key in $REQUIRED_SHARED; do
    if ! vault kv get -field="$key" secret/dcms/shared >/dev/null 2>&1; then
      echo "MISSING  secret/dcms/shared -> $key" >&2
      missing=1
    fi
  done

  for svc in $SERVICES; do
    for key in $(required_keys_for "$svc"); do
      if ! vault kv get -field="$key" "secret/dcms/$svc" >/dev/null 2>&1; then
        echo "MISSING  secret/dcms/$svc -> $key" >&2
        missing=1
      fi
    done
  done

  if [ "$missing" -ne 0 ]; then
    echo >&2
    echo "Refusing the deploy: required secrets are absent. A service started without these" >&2
    echo "does not fail cleanly -- it falls back to a development default or to nothing at all." >&2
    exit 1
  fi

  echo "vault: all required secrets present."
  exit 0
fi

# ---------------------------------------------------------------------------
# role-ids: what a node needs to authenticate its services
# ---------------------------------------------------------------------------

if [ "$MODE" = "role-ids" ]; then
  for svc in $SERVICES; do
    id=$(vault read -field=role_id "auth/approle/role/dcms-$svc/role-id" 2>/dev/null || echo "-")
    printf '%-16s %s\n' "$svc" "$id"
  done
  exit 0
fi

# ---------------------------------------------------------------------------
# apply
# ---------------------------------------------------------------------------

echo "==> Secrets engines"

if ! vault secrets list -format=json | grep -q '"secret/"'; then
  vault secrets enable -path=secret -version=2 kv
  echo "  enabled kv-v2 at secret/"
else
  echo "  kv-v2 at secret/ exists"
fi

if ! vault secrets list -format=json | grep -q '"transit/"'; then
  vault secrets enable transit
  echo "  enabled transit/"
else
  echo "  transit/ exists"
fi

# Existing key. Tenant AI provider keys are encrypted with it, so it must never be recreated:
# a new key of the same name cannot decrypt existing ciphertext, and every tenant's stored
# provider key becomes unreadable. `vault write -f` on an existing key is a no-op.
vault write -f transit/keys/dcms-tenant-secrets >/dev/null
echo "  transit key dcms-tenant-secrets present"

# For encrypting the shared Data Protection key ring at rest (see
# Dcms.Shared.Data.DataProtection). Same warning: recreating it invalidates the key ring,
# which logs everyone out and strands the Forgejo outbox.
vault write -f transit/keys/dcms-dataprotection >/dev/null
echo "  transit key dcms-dataprotection present"

# Tenant Meta (Facebook/Instagram) OAuth tokens. Deliberately a SEPARATE key from
# dcms-tenant-secrets: admin-api needs to decrypt these (it is what calls the Graph API), and
# putting them on the AI key would have handed the admin plane decrypt over every tenant's AI
# provider key as a side effect. Same warning as above -- never recreate it, or every stored
# connection becomes unreadable and every tenant has to reconnect.
vault write -f transit/keys/dcms-social-tokens >/dev/null
echo "  transit key dcms-social-tokens present"

echo "==> Policies"
for svc in $SERVICES; do
  vault policy write "dcms-$svc" - < "policies/dcms-$svc.hcl" >/dev/null
  echo "  dcms-$svc"
done

# The operator/CI policy. Applied here rather than by hand because it is what makes revoking
# the root token possible: everything this script does, it does within dcms-ops.
#
# Except this line, when run BY dcms-ops -- the policy denies writing itself, deliberately, so
# that a compromised ops credential cannot grant itself more. That makes this the one step of
# an otherwise self-sufficient script that needs a credential above ops, which is the correct
# shape: changing what the operator may do is not an operator-level change. So a 403 here is
# reported and skipped rather than failing the run, and everything after it still applies.
if vault policy write dcms-ops - < policies/dcms-ops.hcl >/dev/null 2>&1; then
  echo "  dcms-ops"
else
  echo "  dcms-ops SKIPPED (this token may not rewrite its own policy)"
  echo "    To change it, mint a root token from the recovery keys:"
  echo "      vault operator generate-root -init && vault operator generate-root"
fi

echo "==> AppRole auth"
if ! vault auth list -format=json | grep -q '"approle/"'; then
  vault auth enable approle
  echo "  enabled approle"
else
  echo "  approle enabled"
fi

for svc in $SERVICES; do
  # secret_id_num_uses=0: the id is reusable, because a service restarts and must log in again
  # without a human minting a new one. token_ttl/token_max_ttl keep the ISSUED token short --
  # that is where the expiry protection lives, and unlike the old static token it renews itself
  # simply by the service restarting.
  vault write "auth/approle/role/dcms-$svc" \
    token_policies="dcms-$svc" \
    token_ttl=1h \
    token_max_ttl=4h \
    secret_id_num_uses=0 \
    secret_id_ttl=0 >/dev/null
  echo "  role dcms-$svc -> policy dcms-$svc"
done

# The ops role. Created, never given a secret_id here: minting one on every run would rotate
# the operator's credential behind their back, and this script runs on every deploy.
#
#   vault write -f -field=secret_id auth/approle/role/dcms-ops/secret-id
#
# Longer TTLs than a service role because this is used interactively and by CI jobs that run
# for minutes, not by a process that logs in again whenever it restarts.
vault write auth/approle/role/dcms-ops \
  token_policies="dcms-ops" \
  token_ttl=1h \
  token_max_ttl=8h \
  secret_id_num_uses=0 \
  secret_id_ttl=0 >/dev/null
echo "  role dcms-ops -> policy dcms-ops"

echo
echo "Applied. Secret VALUES are not managed here -- write them once with:"
echo "  vault kv put secret/dcms/shared    <key>=<value> ..."
echo "  vault kv put secret/dcms/<service> <key>=<value> ..."
echo
echo "Then issue each node its credentials:"
echo "  infra/vault/apply.sh --print-role-ids"
echo "  vault write -f auth/approle/role/dcms-<service>/secret-id"
