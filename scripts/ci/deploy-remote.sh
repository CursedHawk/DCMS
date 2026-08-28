#!/usr/bin/env bash
#
# Deploys a resolved image set to a target host. Runs on the CI runner (or a workstation);
# the actual work happens on the target via scripts/deploy.sh.
#
# What gets synced is deliberately NOT the source tree. The old procedure tar'd `src`,
# `apps/admin` and `packages` to vps1 and built there, which meant production built its own
# images from a checkout that was not a git repository -- nothing tied what was running to a
# commit, and the registry images CI had already built went unused. Here the host receives
# only the deployment description (compose files, infra config, scripts) and pulls immutable
# digests for everything else.
#
# Usage:
#   scripts/ci/deploy-remote.sh --env dev|prod --host user@host --images images.yml \
#       [--path ~/baas-dcms] [--registry REG --registry-user U --registry-password-env VAR]
#
# Expects an ssh agent or key already configured.
#
# REGISTRY CREDENTIALS. The host pulls images by digest from a private registry, so it needs
# to be logged in. The obvious way is a long-lived deploy token sitting in the host's
# ~/.docker/config.json forever; this does the other thing. CI passes its own job token, the
# host is logged in for the length of the deploy and logged out again in a trap, so a stolen
# host disk yields no registry credential and a compromised host cannot pull tomorrow's
# images. The token dies with the job either way.

set -euo pipefail

ENVIRONMENT=""
TARGET=""
IMAGES=""
REMOTE_PATH="baas-dcms"
REGISTRY=""
REGISTRY_USER=""
REGISTRY_PASSWORD_ENV=""
EXTRA_ARGS=()

while [ $# -gt 0 ]; do
  case "$1" in
    --env)      ENVIRONMENT="$2"; shift 2 ;;
    --host)     TARGET="$2"; shift 2 ;;
    --images)   IMAGES="$2"; shift 2 ;;
    --path)     REMOTE_PATH="$2"; shift 2 ;;
    --registry) REGISTRY="$2"; shift 2 ;;
    --registry-user) REGISTRY_USER="$2"; shift 2 ;;
    # The NAME of the variable holding the password, never the password itself: an argument
    # is visible in `ps` on the runner for as long as this script runs.
    --registry-password-env) REGISTRY_PASSWORD_ENV="$2"; shift 2 ;;
    *)          EXTRA_ARGS+=("$1"); shift ;;
  esac
done

[ -n "$ENVIRONMENT" ] || { echo "deploy-remote: --env is required" >&2; exit 2; }
[ -n "$TARGET" ]      || { echo "deploy-remote: --host is required" >&2; exit 2; }
[ -n "$IMAGES" ]      || { echo "deploy-remote: --images is required" >&2; exit 2; }
[ -f "$IMAGES" ]      || { echo "deploy-remote: images overlay not found: $IMAGES" >&2; exit 2; }

echo "==> Syncing deployment description to $TARGET:$REMOTE_PATH"

# The host's .env is NOT synced -- it holds that environment's secrets and is the one file
# that must differ between dev and production. --delete is deliberately not used for the same
# reason: it would remove it, along with the .deploy/ rollback state.
rsync -az --info=stats1 \
  docker-compose.yml \
  docker-compose.prod.yml \
  docker-compose.vps.yml \
  "$IMAGES" \
  "$TARGET:$REMOTE_PATH/"

rsync -az --delete --info=stats1 infra/ "$TARGET:$REMOTE_PATH/infra/"
rsync -az --delete --info=stats1 scripts/ "$TARGET:$REMOTE_PATH/scripts/"

IMAGES_NAME=$(basename "$IMAGES")

# ---------------------------------------------------------------------------
# Registry login on the target, for the length of this deploy only
# ---------------------------------------------------------------------------
if [ -n "$REGISTRY" ]; then
  [ -n "$REGISTRY_USER" ] || { echo "deploy-remote: --registry needs --registry-user" >&2; exit 2; }
  [ -n "$REGISTRY_PASSWORD_ENV" ] || { echo "deploy-remote: --registry needs --registry-password-env" >&2; exit 2; }
  REGISTRY_PASSWORD="${!REGISTRY_PASSWORD_ENV-}"
  [ -n "$REGISTRY_PASSWORD" ] || { echo "deploy-remote: \$$REGISTRY_PASSWORD_ENV is empty" >&2; exit 2; }

  # Log out again whatever happens. Without the trap a failed deploy leaves the credential
  # on the host, which is the state this whole approach exists to avoid.
  cleanup_registry() {
    ssh "$TARGET" "docker logout '$REGISTRY' >/dev/null 2>&1" || true
  }
  trap cleanup_registry EXIT

  echo "==> Logging the target in to $REGISTRY (for this deploy only)"
  # --password-stdin, so the token never appears in the host's process list either.
  printf '%s' "$REGISTRY_PASSWORD" \
    | ssh "$TARGET" "docker login -u '$REGISTRY_USER' --password-stdin '$REGISTRY'"
  unset REGISTRY_PASSWORD
fi

echo "==> Validating the resolved configuration on the target"
# Cheap, and it fails before anything is torn down: a missing variable in the host's .env or
# a disagreement between the overlays surfaces here rather than half way through a roll.
ssh "$TARGET" "cd '$REMOTE_PATH' && chmod +x scripts/deploy.sh scripts/ci/*.sh 2>/dev/null; scripts/deploy.sh --env '$ENVIRONMENT' --images '$IMAGES_NAME' --check"

echo "==> Deploying"
ssh "$TARGET" "cd '$REMOTE_PATH' && scripts/deploy.sh --env '$ENVIRONMENT' --images '$IMAGES_NAME' ${EXTRA_ARGS[*]:-}"

echo "==> Deployed $IMAGES_NAME to $TARGET ($ENVIRONMENT)"
# The EXIT trap logs the host out from here.
