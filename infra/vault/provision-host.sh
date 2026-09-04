#!/usr/bin/env bash
#
# Issues one host its AppRole credentials and writes them into .env. One command, once per
# service, per host.
#
# WHY THIS EXISTS. apply.sh creates the policies and the roles; everything after that was
# prose in its closing message -- read a role_id, mint a secret_id, edit .env, do not paste
# the wrong one. That is four manual steps per service and the last of them is a secret going
# through a human's clipboard. This does the same work with the credential never leaving the
# host it belongs to.
#
# WHAT IT DELIBERATELY DOES NOT DO. It does not hold a token, does not write one to disk, and
# does not weaken the separation apply.sh set up: the admin token is supplied for THIS RUN by
# the operator, in the environment, and every service still gets its own role bound to its own
# single-path policy. Nothing here grants a service anything it did not already have.
#
# Usage, from the repository root on the target host:
#
#   VAULT_TOKEN=<admin token> infra/vault/provision-host.sh platform-api log-janitor
#   VAULT_TOKEN=<admin token> infra/vault/provision-host.sh --all
#
# The token is read from the environment and never echoed. Prefer a short-lived one:
#
#   VAULT_TOKEN=$(vault token create -policy=dcms-ops -ttl=15m -field=token) \
#     infra/vault/provision-host.sh --all
#
# Re-running is safe. A service that already has BOTH ids in .env is left alone, because
# minting a fresh secret_id and rewriting .env would invalidate nothing but would churn a
# working credential for no reason. Pass --rotate to replace them on purpose.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."
REPO_ROOT=$(pwd)
ENV_FILE="$REPO_ROOT/.env"

export VAULT_ADDR="${VAULT_ADDR:-http://127.0.0.1:8200}"
export PATH="$REPO_ROOT/infra/vault/bin:$PATH"

# The same list apply.sh creates roles from. It used to be a second copy here, and it was
# missing `edge` -- so --all issued credentials for every service except the one terminating
# TLS, and every handshake on the platform was refused. See infra/vault/services.sh.
. "$REPO_ROOT/infra/vault/services.sh"
ALL_SERVICES="$DCMS_VAULT_SERVICES"

ROTATE=0
SERVICES=""
for arg in "$@"; do
  case "$arg" in
    --all)    SERVICES="$ALL_SERVICES" ;;
    --rotate) ROTATE=1 ;;
    -*)       echo "provision-host.sh: unknown argument $arg" >&2; exit 2 ;;
    *)        SERVICES="$SERVICES $arg" ;;
  esac
done

[ -n "${SERVICES// /}" ] || { echo "provision-host.sh: name at least one service, or --all" >&2; exit 2; }
: "${VAULT_TOKEN:?VAULT_TOKEN must be set for this run (it is never stored)}"
[ -f "$ENV_FILE" ] || { echo "provision-host.sh: no .env at $ENV_FILE" >&2; exit 2; }

# Fail before touching anything if the token cannot do the work.
vault token lookup >/dev/null 2>&1 || {
  echo "provision-host.sh: VAULT_TOKEN is not valid against $VAULT_ADDR" >&2; exit 1; }

# VAULT_ROLE_ID_PLATFORM_API from platform-api.
env_key() { echo "$1" | tr 'a-z-' 'A-Z_'; }

# Sets KEY=value in .env: replaces the line if present, appends if not. Values are never
# echoed -- only the key name and whether it changed.
set_env() {
  key="$1"; value="$2"
  if grep -qE "^$key=" "$ENV_FILE"; then
    # A temp file in the same directory, then mv: an interrupted in-place edit of .env is a
    # host that cannot start anything.
    tmp="$ENV_FILE.provision.$$"
    grep -vE "^$key=" "$ENV_FILE" > "$tmp"
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
    chmod --reference="$ENV_FILE" "$tmp" 2>/dev/null || chmod 600 "$tmp"
    mv "$tmp" "$ENV_FILE"
  else
    printf '%s=%s\n' "$key" "$value" >> "$ENV_FILE"
  fi
}

echo "==> Provisioning AppRole credentials into .env"
for svc in $SERVICES; do
  upper=$(env_key "$svc")
  role_key="VAULT_ROLE_ID_$upper"
  secret_key="VAULT_SECRET_ID_$upper"

  role_id=$(vault read -field=role_id "auth/approle/role/dcms-$svc/role-id" 2>/dev/null || true)
  if [ -z "$role_id" ]; then
    echo "  SKIP    $svc -- no AppRole. Run infra/vault/apply.sh first."
    continue
  fi

  have_role=$(grep -E "^$role_key=." "$ENV_FILE" 2>/dev/null || true)
  have_secret=$(grep -E "^$secret_key=." "$ENV_FILE" 2>/dev/null || true)
  if [ -n "$have_role" ] && [ -n "$have_secret" ] && [ "$ROTATE" -eq 0 ]; then
    echo "  keep    $svc (both ids already set; --rotate to replace)"
    continue
  fi

  secret_id=$(vault write -f -field=secret_id "auth/approle/role/dcms-$svc/secret-id")
  set_env "$role_key" "$role_id"
  set_env "$secret_key" "$secret_id"
  unset secret_id
  echo "  issued  $svc -> $role_key, $secret_key"
done

echo
echo "Done. The token was used for this run only and was not written anywhere."
echo "Roll the services that changed:  scripts/deploy.sh --env <env>"
