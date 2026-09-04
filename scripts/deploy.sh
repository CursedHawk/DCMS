#!/usr/bin/env bash
#
# Deploy the DCMS stack on a target host.
#
# This script replaces the untracked run-deploy.sh / run-deploy-full.sh that
# lived only in ~/baas-dcms on vps1. It runs ON the target host, from the repo
# root. CI delivers the tree with rsync and then invokes it over ssh.
#
# It encodes three things that have each already cost an outage:
#
#   1. The compose invocation always passes the FULL overlay set. A partial
#      -f set silently drops overrides and has taken production down before.
#   2. Images are built ONE AT A TIME. vps1 is 4 cores / 7.6 GB with no swap;
#      building more than two services concurrently thrashes the box into an
#      OOM kill, and the kernel does not necessarily kill the builder.
#   3. Services whose configuration is a bind-mounted FILE are recreated, not
#      reloaded. A reload reads the stale inode after an rsync replaces the
#      file, so the container keeps running the previous config while every
#      outward sign says the deploy succeeded.
#
# Usage:
#   scripts/deploy.sh [options] [service ...]
#
#   --env dev|prod       Overlay set to use. Default: $DCMS_ENV, else prod.
#   --check              Preflight only: resolve and validate the overlay set,
#                        then stop. Changes nothing. Run this first.
#   --no-migrate         Skip the provisioning and schema migration jobs -- JetStream
#                        streams, MinIO buckets, then the two EF migration jobs. They
#                        run by default: the services no longer migrate at startup, so
#                        skipping this rolls new code against an old schema.
#   --build              Build images locally, serially, before rolling.
#   --pull               Pull images from the registry before rolling.
#   --images FILE        Extra compose overlay pinning image digests. Recorded
#                        for rollback. Implies --pull.
#   --rollback           Redeploy the previously recorded digest overlay.
#   --no-health          Skip the post-deploy health gate.
#   --recreate-vault     Recreate Vault even when it is Shamir-sealed. Doing so
#                        SEALS it, and every service reads its config from Vault
#                        at startup -- so the platform stays down until someone
#                        unseals it by hand. Attended Vault-config changes only.
#   --timeout SECONDS    Health gate budget per service. Default 120.
#
# With no service arguments every service is deployed.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
REPO_ROOT=$(pwd)

# ---------------------------------------------------------------------------
# Service inventory
# ---------------------------------------------------------------------------

# Built from this repo, in dependency-ish order. Serial builds follow this order.
APP_SERVICES=(
  identity admin-api content-api ai-gateway platform-api
  media-worker email-worker site-builder site-host edge admin-spa platform-spa
)

# Configuration arrives as a bind-mounted file. These must be force-recreated
# after the tree is synced -- see lesson 3 above.
CONFIG_MOUNTED_SERVICES=(alloy prometheus loki tempo grafana nats vault)

# Health-gated after a roll. The workers expose /health/live but carry no
# inbound traffic, so a slow start is not an outage; they are still checked.
#
# edge, admin-spa and forgejo are here because "every service is healthy" was not the same
# claim as "the platform works". All eight .NET services can pass while nothing is reachable
# from outside, and the deploy still reports green. The edge's probe is /health, which runs
# RouteTableHealthCheck -- an ingress that loaded no routes is unhealthy, not merely alive.
#
# Not gated, because their images ship no shell and no HTTP client to probe with: nats, loki,
# tempo, alloy. Losing them costs telemetry rather than service, which is the right side of
# the line to be stuck on -- but it is a gap, not a decision.
HEALTH_GATED_SERVICES=(
  identity admin-api content-api ai-gateway platform-api
  media-worker email-worker site-builder site-host edge
  admin-spa platform-spa forgejo
)

DEPLOY_STATE_DIR="${DCMS_DEPLOY_STATE_DIR:-$REPO_ROOT/.deploy}"

# ---------------------------------------------------------------------------
# Options
# ---------------------------------------------------------------------------

ENVIRONMENT="${DCMS_ENV:-prod}"
DO_BUILD=0
DO_CHECK=0
DO_MIGRATE=1
DO_PULL=0
DO_HEALTH=1
DO_ROLLBACK=0
FORCE_RECREATE_VAULT=0
VAULT_SEAL_TYPE=""
IMAGES_FILE=""
HEALTH_TIMEOUT=120
TARGET_SERVICES=()

while [ $# -gt 0 ]; do
  case "$1" in
    --env)        ENVIRONMENT="$2"; shift 2 ;;
    --check)      DO_CHECK=1; shift ;;
    --no-migrate) DO_MIGRATE=0; shift ;;
    --build)      DO_BUILD=1; shift ;;
    --pull)       DO_PULL=1; shift ;;
    --images)     IMAGES_FILE="$2"; DO_PULL=1; shift 2 ;;
    --rollback)   DO_ROLLBACK=1; shift ;;
    --no-health)  DO_HEALTH=0; shift ;;
    --recreate-vault) FORCE_RECREATE_VAULT=1; shift ;;
    --timeout)    HEALTH_TIMEOUT="$2"; shift 2 ;;
    -h|--help)    sed -n '2,43p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*)           echo "deploy: unknown option $1" >&2; exit 2 ;;
    *)            TARGET_SERVICES+=("$1"); shift ;;
  esac
done

log()  { printf '\n\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m warn:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# Compose invocation -- always the full overlay set
# ---------------------------------------------------------------------------

case "$ENVIRONMENT" in
  prod|dev)
    # Both deployed environments use the production profile. They differ in the
    # host overlay and in the values Vault hands them, not in the profile: a dev
    # environment that runs a different code path is not a rehearsal.
    COMPOSE_FILES=(-f docker-compose.yml -f docker-compose.prod.yml)
    [ -f docker-compose.vps.yml ] && COMPOSE_FILES+=(-f docker-compose.vps.yml)
    ;;
  local)
    # Bare `docker compose` picks up docker-compose.override.yml automatically.
    COMPOSE_FILES=()
    # The dev overlay leaves Tenancy:Migrate and Identity:Migrate on, so services still
    # migrate at startup and a separate job would only duplicate the work.
    DO_MIGRATE=0
    ;;
  *)
    die "unknown environment '$ENVIRONMENT' (expected dev, prod or local)"
    ;;
esac

if [ -n "$IMAGES_FILE" ]; then
  [ -f "$IMAGES_FILE" ] || die "images overlay not found: $IMAGES_FILE"
  COMPOSE_FILES+=(-f "$IMAGES_FILE")
fi

compose() { docker compose "${COMPOSE_FILES[@]}" "$@"; }

# ---------------------------------------------------------------------------
# Rollback: swap the recorded digest overlays back and redeploy
# ---------------------------------------------------------------------------

if [ "$DO_ROLLBACK" = 1 ]; then
  PREVIOUS="$DEPLOY_STATE_DIR/images.previous.yml"
  [ -f "$PREVIOUS" ] || die "no previous deployment recorded at $PREVIOUS"
  log "Rolling back to the previously deployed digests"
  exec "$0" --env "$ENVIRONMENT" --images "$PREVIOUS" --timeout "$HEALTH_TIMEOUT"
fi

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

log "Preflight ($ENVIRONMENT)"

# Reads one key out of .env, or nothing when it is absent.
#
# Not merely tidier than an inline grep -- it fixes a way this script could fail with no
# message at all. Under `set -euo pipefail`, `x="$(grep KEY .env | tail -1 | cut ...)"` fails
# the whole pipeline when grep matches nothing, the assignment inherits that status, and -e
# exits silently. Every check below is about a variable that is legitimately ABSENT sometimes,
# so the very case each one exists to warn about was the case that killed the deploy before it
# could warn. That is what a PLATFORM_HOST-less .env did: "Preflight (dev)" and then exit 1.
env_value() {
  grep -E "^$1=" .env 2>/dev/null | tail -1 | cut -d= -f2- || true
}


docker compose version >/dev/null 2>&1 || die "docker compose plugin not available"

# Compose substitutes ${VAR} from .env. Two NATS passwords use the ${VAR:?}
# form, so a missing one fails the whole invocation rather than silently
# starting a server nobody can authenticate against.
if [ "$ENVIRONMENT" != "local" ] && [ ! -f .env ]; then
  die ".env not found in $REPO_ROOT -- compose has nothing to substitute"
fi

# `config` fully resolves the overlay set and every substitution. If the files
# disagree or a required variable is unset, this is where it surfaces -- before
# anything is torn down.
compose config --quiet || die "compose configuration is invalid; nothing was changed"

# DCMS_ENV / DCMS_HOST become Prometheus external_labels, stamped onto every series
# this host stores and every alert it fires. dev and prod run identical stacks from
# identical config, so these labels are the only thing that tells their telemetry apart.
#
# Unset is survivable -- compose falls back to `unknown`, which is visibly unconfigured.
# A MISMATCH is not: deploying the prod overlay onto a host whose .env still says
# DCMS_ENV=dev produces prod alerts that name dev, and they look entirely plausible.
if [ "$ENVIRONMENT" != "local" ]; then
  env_declared="$(env_value DCMS_ENV)"
  host_declared="$(env_value DCMS_HOST)"
  if [ -n "$env_declared" ] && [ "$env_declared" != "$ENVIRONMENT" ]; then
    die "DCMS_ENV in .env is '$env_declared' but this is a '$ENVIRONMENT' deploy.
     Telemetry from this host would be labelled as the other environment.
     Fix .env, or pass --env $env_declared if the overlay set is what is wrong."
  fi
  if [ -z "$env_declared" ] || [ -z "$host_declared" ]; then
    warn "DCMS_ENV/DCMS_HOST not both set in .env -- this host's metrics and alerts will be labelled 'unknown'"
  fi

  # ADMIN_HOST is PUBLIC_BASE_URL without the scheme. The edge routes on the bare hostname;
  # identity needs the URL for the issuer it stamps into every token. If they disagree, the
  # edge serves a certificate for one name while tokens claim another, and the symptom is a
  # login that loops rather than an error anyone can read.
  base_url="$(env_value PUBLIC_BASE_URL)"
  admin_host="$(env_value ADMIN_HOST)"
  if [ -n "$base_url" ]; then
    case "$base_url" in
      */) die "PUBLIC_BASE_URL must not end in a slash (got '$base_url').
     The compose files append their own paths to it." ;;
    esac
    base_host="${base_url#https://}"; base_host="${base_host#http://}"
    if [ -n "$admin_host" ] && [ "$admin_host" != "$base_host" ]; then
      die "ADMIN_HOST is '$admin_host' but PUBLIC_BASE_URL is '$base_url'.
     The edge would serve '$admin_host' while identity issues tokens for '$base_host'."
    fi
    [ -z "$admin_host" ] && warn "ADMIN_HOST unset -- the edge falls back to its built-in default, which may not be '$base_host'"
  fi

  # There is deliberately no check here for the platform console's database password. It is
  # not in .env any more -- it lives in Vault, in secret/dcms/platform-api and the matching
  # Platform__DbPassword under secret/dcms/admin-api, and `infra/vault/apply.sh --check` is
  # what asserts both are present. A grep of .env would only ever find its absence.

  # Not fatal: an unset PLATFORM_HOST means the edge uses its built-in default, which simply
  # fails ACME for a name this host does not own. Every other route keeps working.
  platform_host="$(env_value PLATFORM_HOST)"
  [ -z "$platform_host" ] \
    && warn "PLATFORM_HOST unset -- the platform console falls back to the edge's built-in default hostname, and needs a DNS A record pointing here before it can get a certificate"

  # AUTH_HOST is not like the others. The rest are addresses a console is reached at; this one
  # is the token issuer, so an unset value is not "a page nobody visits" -- it is every service
  # validating `iss` against a hostname that has no DNS record and nobody being able to sign in.
  auth_host="$(env_value AUTH_HOST)"
  [ -z "$auth_host" ] \
    && warn "AUTH_HOST unset -- authentication falls back to the built-in default, which is also the token issuer every service validates against. It needs a DNS A record pointing here, and Google's authorized redirect URI must be https://<AUTH_HOST>/signin-google"

  # Every service that asks for an AppRole must actually have one.
  #
  # THIS IS THE CHECK THAT WAS MISSING. `edge` was added to infra/vault/apply.sh but not to
  # provision-host.sh's own copy of the service list, so `--all` created the policy, the role
  # and the dcms-tls-keys transit key and then quietly issued credentials to every service
  # except the one terminating TLS. VAULT_ROLE_ID_EDGE was never written to .env, the edge
  # could neither store nor read a private key, and every TLS handshake on every hostname was
  # refused -- ERR_CONNECTION_FAILED across the whole platform, from an empty string.
  #
  # Read from the RESOLVED compose config rather than from .env, so it asks the question the
  # containers will actually be started with. A service that legitimately uses a token instead
  # (dev) declares VAULT_TOKEN with a value and is not flagged.
  # Flatten the resolved config to "<service> <VAULT_*> <value>" triples, then judge them in
  # bash where the logic is readable.
  vault_env=$(compose config 2>/dev/null | awk '
    /^  [a-zA-Z0-9_.-]+:$/ { svc=$1; sub(/:$/, "", svc) }
    /^ +VAULT_(ROLE_ID|SECRET_ID|TOKEN): / {
      key=$1; sub(/:$/, "", key); gsub(/"/, "", $2); print svc, key, $2
    }')

  missing_approles=""
  for svc in $(printf '%s\n' "$vault_env" | awk '$2 == "VAULT_ROLE_ID" { print $1 }' | sort -u); do
    # A service handed a token instead of an AppRole is configured, just differently (dev).
    has_token=$(printf '%s\n' "$vault_env" | awk -v s="$svc" '$1 == s && $2 == "VAULT_TOKEN" && NF == 3 { print "y" }')
    [ -n "$has_token" ] && continue
    role=$(printf '%s\n' "$vault_env" | awk -v s="$svc" '$1 == s && $2 == "VAULT_ROLE_ID" && NF == 3 { print "y" }')
    secret=$(printf '%s\n' "$vault_env" | awk -v s="$svc" '$1 == s && $2 == "VAULT_SECRET_ID" && NF == 3 { print "y" }')
    [ -n "$role" ] && [ -n "$secret" ] || missing_approles="$missing_approles $svc"
  done

  # A WARNING, not a failure, and that distinction was learned the hard way: making this
  # fatal blocked every deploy on the host -- including the one carrying the fix. A preflight
  # must never be the reason a repair cannot ship. The service's own startup guard is what
  # fails the health gate; this exists so the cause is named before the roll rather than found
  # in a container log afterwards.
  if [ -n "${missing_approles// /}" ]; then
    warn "no Vault AppRole credentials for:${missing_approles}
       These services read their configuration -- and the edge its TLS private keys -- through
       Vault, and an empty role id means every read fails. For the edge that is every TLS
       handshake refused, on every hostname. Issue them on this host:
         VAULT_TOKEN=<admin token> infra/vault/apply.sh
       apply.sh provisions any service that is missing them, so nothing has to be named."
  fi
fi

echo "  compose: docker compose ${COMPOSE_FILES[*]}"
[ -n "$IMAGES_FILE" ] && echo "  images:  $IMAGES_FILE"

if [ "$DO_CHECK" = 1 ]; then
  log "Configuration is valid. Nothing was changed (--check)."
  exit 0
fi

# ---------------------------------------------------------------------------
# Which services are we touching?
# ---------------------------------------------------------------------------

if [ ${#TARGET_SERVICES[@]} -eq 0 ]; then
  BUILD_LIST=("${APP_SERVICES[@]}")
  ROLL_ALL=1
else
  BUILD_LIST=()
  for svc in "${TARGET_SERVICES[@]}"; do
    for app in "${APP_SERVICES[@]}"; do
      [ "$svc" = "$app" ] && BUILD_LIST+=("$svc")
    done
  done
  ROLL_ALL=0
fi

# ---------------------------------------------------------------------------
# Images
# ---------------------------------------------------------------------------

if [ "$DO_PULL" = 1 ]; then
  log "Pulling images"
  if [ "$ROLL_ALL" = 1 ]; then
    compose pull --ignore-buildable
  else
    compose pull --ignore-buildable "${TARGET_SERVICES[@]}"
  fi
fi

if [ "$DO_BUILD" = 1 ]; then
  log "Building ${#BUILD_LIST[@]} service(s) -- serially, one at a time"
  for svc in "${BUILD_LIST[@]}"; do
    echo "  building $svc"
    # Deliberately not backgrounded and deliberately not batched. See lesson 2.
    compose build "$svc" || die "build failed for $svc; nothing was rolled"
  done
fi

# ---------------------------------------------------------------------------
# Vault seal state
# ---------------------------------------------------------------------------

# Every service reads its configuration from Vault at startup and refuses to boot on a 503,
# so deploying into a sealed Vault rolls the whole stack into a crash loop. That is worth one
# cheap check first: sys/seal-status needs no token, so this costs nothing and no credential.
#
# Deliberately NOT a check that the right secrets exist -- that would need a token with read
# on them, and inventing a privileged CI credential to assert a precondition is a worse trade
# than letting the services' own startup guards fail the health gate. What this catches is the
# common case: the host rebooted and nobody unsealed it.
if [ "$ENVIRONMENT" != "local" ] && compose ps --services 2>/dev/null | grep -qx vault; then
  log "Checking Vault seal state"
  seal_status=$(compose exec -T vault vault status -format=json 2>/dev/null || echo '')
  if [ -z "$seal_status" ]; then
    warn "could not read Vault's seal status; it may not be running yet"
  elif echo "$seal_status" | grep -q '"sealed": *true'; then
    die "Vault is SEALED. Services read their configuration from it at startup and will not boot.
       Unseal it first (\`vault operator unseal\`, or configure Transit auto-unseal -- see
       infra/vault/server/seal-transit.hcl), then re-run this deploy."
  else
    # shamir | transit | awskms | ... -- decides whether recreating Vault is safe below.
    VAULT_SEAL_TYPE=$(echo "$seal_status" | sed -n 's/.*"type": *"\([a-z]*\)".*/\1/p' | head -1)
    echo "  vault is unsealed (seal: ${VAULT_SEAL_TYPE:-unknown})"
  fi
fi

# ---------------------------------------------------------------------------
# Mode B build sandbox
# ---------------------------------------------------------------------------

# site-builder launches this image itself, with `docker run` against the shared daemon, under
# the fixed name in DCMS_BUILD_SANDBOX_IMAGE. Compose never starts it -- it is profile-gated
# with `entrypoint: true` purely so there is somewhere to build it -- so a `compose pull` does
# not fetch it and the host would be left with whatever it built by hand months ago.
#
# DCMS_BUILD_REQUIRE_SANDBOX=true makes a missing image a failed BUILD, not a failed deploy,
# which surfaces as a tenant's site silently not publishing.
SANDBOX_LOCAL_TAG="${DCMS_BUILD_SANDBOX_IMAGE:-dcms/site-build-sandbox:latest}"

if [ -n "$IMAGES_FILE" ] && grep -q '^  site-build-sandbox:' "$IMAGES_FILE"; then
  sandbox_ref=$(awk '/^  site-build-sandbox:/{getline; print $2; exit}' "$IMAGES_FILE")
  if [ -n "$sandbox_ref" ]; then
    log "Fetching the Mode B build sandbox"
    echo "  $sandbox_ref"
    # A pull failure is only fatal if the host does not already have this exact digest.
    #
    # CI logs the target host into the registry and out again in an EXIT trap, so an
    # attended deploy -- a config-only change rolled by hand, or a rollback during an
    # incident -- runs with no registry credentials at all. Every other image in the
    # overlay is already local by then and compose does not re-fetch it; this one is
    # pulled explicitly, so it was the single thing turning "re-apply the compose files"
    # into "first go and find a deploy token". The digest is pinned either way, so
    # accepting the local copy accepts exactly the image the overlay names.
    if ! docker pull "$sandbox_ref"; then
      if docker image inspect "$sandbox_ref" >/dev/null 2>&1; then
        warn "could not pull the build sandbox; the pinned digest is already present locally, continuing"
      else
        die "could not pull the build sandbox image, and it is not present locally"
      fi
    fi
    # Retagged to the stable local name so site-builder's configuration does not have to
    # carry a digest that changes on every release.
    docker tag "$sandbox_ref" "$SANDBOX_LOCAL_TAG"
    echo "  tagged as $SANDBOX_LOCAL_TAG"
  fi
fi

# Post-condition, deliberately OUTSIDE the block above, because the interesting failure is
# not the pull -- it is the image going missing between deploys.
#
# This image is the only one on the host that no container ever references: site-builder
# starts it with `docker run` per build, so it is unreferenced whenever a build is not in
# flight, which is almost always. `docker image prune -a` -- the obvious thing to reach for
# on a host whose disk alert is firing, and which leaves every service image alone because a
# running container holds it -- deletes exactly this one and nothing else. It was found
# missing on vps1 that way, with a green deploy history and no error anywhere: the symptom is
# a tenant's Mode B publish failing, days later, with a message about the sandbox.
#
# So check the tag resolves rather than trusting that a pull ran at some point in the past.
# A config-only re-apply with no --images has nothing to pull and still has to be told.
if ! docker image inspect "$SANDBOX_LOCAL_TAG" >/dev/null 2>&1; then
  if [ -n "$IMAGES_FILE" ]; then
    die "the Mode B build sandbox ($SANDBOX_LOCAL_TAG) is not present after the pull.
     Every ReactApp site publish will fail until it is. Re-run the deploy from CI, which
     holds the registry credentials this host deliberately does not."
  fi
  warn "the Mode B build sandbox ($SANDBOX_LOCAL_TAG) is missing and this run has no --images
     overlay to pull it from. ReactApp (Mode B) site publishes will fail until a full deploy
     restores it; Mode A and Mode C are unaffected."
fi

# ---------------------------------------------------------------------------
# Message streams and object storage -- BEFORE anything is rolled
# ---------------------------------------------------------------------------

# nats-init and minio-init are ordinary services with no profile, so the `up -d` further
# down starts them regardless. Running them HERE, explicitly, buys two things:
#
#   1. An exit code. As a side effect of `up -d` their failure is invisible -- the deploy
#      reports success, and the stream that was never created surfaces days later as a
#      consumer receiving nothing, which reads as an application bug.
#   2. A container built from the CURRENT definition. `up -d` restarts the existing exited
#      one, and both scripts arrive by rsync -- so the deploy that provisions a new stream
#      is precisely the one at risk of re-running the copy that does not know about it.
#
# `run --rm` is the same idiom as the migration jobs below, for the same reason. Both
# scripts are idempotent, so the later `up -d` re-running them costs nothing.
if [ "$DO_MIGRATE" = 1 ] && [ "$ROLL_ALL" = 1 ] && [ "$ENVIRONMENT" != "local" ]; then
  log "Provisioning message streams and object storage"
  # Resolved once, and matched with a here-string rather than a pipe: `compose ... | grep -q`
  # under `set -o pipefail` reports the pipeline as failed when grep exits on the first match
  # and compose takes SIGPIPE, so a SUCCESSFUL match could silently skip the job.
  DEFINED_SERVICES=$(compose config --services 2>/dev/null || true)
  for job in nats-init minio-init; do
    grep -qx "$job" <<<"$DEFINED_SERVICES" || continue
    echo "  running $job"
    if ! compose run --rm "$job"; then
      die "provisioning job '$job' failed; nothing was rolled and the running stack is untouched"
    fi
  done
fi

# ---------------------------------------------------------------------------
# Schema migrations -- BEFORE anything is rolled
# ---------------------------------------------------------------------------

# Only when deploying the whole stack. Rolling one service does not re-migrate: if that
# service needed a schema change, the change belongs to a full deploy.
if [ "$DO_MIGRATE" = 1 ] && [ "$ROLL_ALL" = 1 ]; then
  log "Running schema migration jobs"

  # Order matters, and it is the dependency order of the schema itself:
  #
  #   postgres-bootstrap  creates schemas, extensions and the non-owner roles
  #   migrate             creates the tables inside them, then applies RLS policies,
  #                       the obs views and the audit partitions
  #   identity-migrate    creates identity's own tables and seeds them -- which writes an
  #                       audit row, so the audit schema has to exist first
  for job in postgres-bootstrap migrate identity-migrate; do
    echo "  running $job"
    if ! compose --profile migrate run --rm "$job"; then
      die "migration job '$job' failed; nothing was rolled and the running stack is untouched"
    fi
  done
elif [ "$DO_MIGRATE" = 0 ]; then
  warn "skipping migrations (--no-migrate); new code may be rolling against an old schema"
fi

# ---------------------------------------------------------------------------
# Record what we are about to deploy, so --rollback has somewhere to go
# ---------------------------------------------------------------------------

if [ -n "$IMAGES_FILE" ]; then
  mkdir -p "$DEPLOY_STATE_DIR"
  CURRENT="$DEPLOY_STATE_DIR/images.current.yml"
  # Only rotate when the incoming set actually differs, so a repeated deploy of
  # the same digests does not overwrite the last genuinely different version
  # and leave --rollback pointing at what is already running.
  if [ -f "$CURRENT" ] && ! cmp -s "$CURRENT" "$IMAGES_FILE"; then
    cp "$CURRENT" "$DEPLOY_STATE_DIR/images.previous.yml"
  fi
  # ...and skip the copy entirely when the overlay handed to us IS the recorded one.
  # `cp a a` is an error, and under `set -e` that error aborted the deploy at the last
  # step before rolling -- after the migration jobs had already run. Re-applying the
  # current digests is not an odd thing to do: it is what an attended config-only deploy
  # does, and what someone re-running a half-finished deploy does.
  if [ ! "$IMAGES_FILE" -ef "$CURRENT" ]; then
    cp "$IMAGES_FILE" "$CURRENT"
  fi
fi

# ---------------------------------------------------------------------------
# Roll
# ---------------------------------------------------------------------------

# `compose up -d` recreates any container whose DEFINITION changed -- a new image digest, but
# also a changed mount or command. That is not the same set as the --force-recreate list below,
# and the first pipeline deploy proved it: Vault's config mount moved from a file to a
# directory, so the roll recreated Vault, sealed it, and the platform stayed down until someone
# unsealed it by hand. The guard further down never ran, because it only covers the explicit
# force-recreate loop.
#
# Excluding vault from the service list would not help either -- services depends_on it, so
# compose would bring it up regardless. So: ask compose what it intends to do, and refuse the
# whole deploy if that includes recreating a Shamir-sealed Vault. Failing before anything is
# touched is much better than discovering it afterwards from a stack that will not boot.
if [ "${VAULT_SEAL_TYPE:-}" = "shamir" ] && [ "$FORCE_RECREATE_VAULT" != 1 ]; then
  # Compose prints one line per container, e.g. " Container dcms-vault-1  Recreate".
  # The `vault-[0-9]` anchor matches the Vault service itself and deliberately not
  # dcms-vault-init-1, which is a one-shot job and is free to be recreated.
  if compose up -d --remove-orphans --dry-run 2>&1 \
     | grep -qE "Container [A-Za-z0-9_-]*[-_]vault-[0-9]+ +Recreate"; then
    die "This deploy would recreate Vault, which is SHAMIR-sealed -- recreating it seals it, and
     every service reads its configuration from Vault at startup, so the platform would not
     come back until someone unsealed it by hand. Nothing has been changed.

     Do it attended, then re-run this deploy:
       docker compose ${COMPOSE_FILES[*]} up -d --force-recreate --no-deps vault
       ./unseal-vault.sh

     Or pass --recreate-vault to accept the outage, or configure Transit auto-unseal
     (infra/vault/server/seal-transit.hcl) so there is nothing to unseal."
  fi
fi

log "Rolling services"
if [ "$ROLL_ALL" = 1 ]; then
  compose up -d --remove-orphans
else
  compose up -d --no-deps "${TARGET_SERVICES[@]}"
fi

# Configuration is a bind-mounted file for these; a reload would read the stale
# inode left behind when rsync replaced it. Recreate instead. See lesson 3.
log "Recreating services whose config is a bind-mounted file"
for svc in "${CONFIG_MOUNTED_SERVICES[@]}"; do
  # Not every overlay defines every one of these (the nats config file is
  # vps-only), so skip what this environment does not have.
  if compose ps --services 2>/dev/null | grep -qx "$svc"; then
    if [ "$ROLL_ALL" = 1 ] || printf '%s\n' "${TARGET_SERVICES[@]}" | grep -qx "$svc"; then
      # Recreating a SHAMIR-sealed Vault seals it, and every service reads its configuration
      # from Vault at startup -- so an unattended pipeline deploy would take the whole platform
      # down and leave it down until a human unseals it by hand. A stale Vault config mount is
      # a far smaller problem than that, so skip it and say so.
      #
      # Under Transit auto-unseal there is nothing to skip: Vault comes back unsealed on its
      # own, which is the entire reason that seal exists.
      if [ "$svc" = "vault" ] && [ "${VAULT_SEAL_TYPE:-}" = "shamir" ] && [ "$FORCE_RECREATE_VAULT" != 1 ]; then
        warn "skipping vault recreate -- it is Shamir-sealed, and recreating it would seal the platform.
       Vault config changes need a deliberate, attended restart:
         docker compose <the full -f set> up -d --force-recreate --no-deps vault && ./unseal-vault.sh
       Or pass --recreate-vault to accept the outage, or configure Transit auto-unseal."
        continue
      fi
      echo "  recreating $svc"
      compose up -d --force-recreate --no-deps "$svc"
    fi
  fi
done

# ---------------------------------------------------------------------------
# Health gate
# ---------------------------------------------------------------------------

if [ "$DO_HEALTH" = 0 ]; then
  log "Health gate skipped (--no-health)"
  exit 0
fi

log "Health gate (${HEALTH_TIMEOUT}s per service)"

if [ "$ROLL_ALL" = 1 ]; then
  CHECK_LIST=("${HEALTH_GATED_SERVICES[@]}")
else
  CHECK_LIST=()
  for svc in "${TARGET_SERVICES[@]}"; do
    for gated in "${HEALTH_GATED_SERVICES[@]}"; do
      [ "$svc" = "$gated" ] && CHECK_LIST+=("$svc")
    done
  done
fi

FAILED=()
for svc in "${CHECK_LIST[@]}"; do
  cid=$(compose ps -q "$svc" 2>/dev/null | head -1)
  if [ -z "$cid" ]; then
    warn "$svc: no container -- not deployed in this environment?"
    continue
  fi

  deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
  status=""
  while [ "$(date +%s)" -lt "$deadline" ]; do
    # `docker inspect` rather than `compose ps --format json`: the JSON field
    # names have moved between compose releases and this has to work on
    # whatever the target host happens to be running.
    status=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$cid" 2>/dev/null || echo "gone")
    case "$status" in
      healthy|running) break ;;
      exited|dead|gone) break ;;
    esac
    sleep 3
  done

  case "$status" in
    healthy|running) printf '  \033[1;32mok\033[0m       %s (%s)\n' "$svc" "$status" ;;
    *)               printf '  \033[1;31mFAILED\033[0m   %s (%s)\n' "$svc" "$status"; FAILED+=("$svc") ;;
  esac
done

if [ ${#FAILED[@]} -gt 0 ]; then
  warn "unhealthy after deploy: ${FAILED[*]}"
  for svc in "${FAILED[@]}"; do
    echo "--- last 40 log lines: $svc ---" >&2
    compose logs --tail 40 "$svc" >&2 || true
  done
  if [ -f "$DEPLOY_STATE_DIR/images.previous.yml" ]; then
    warn "roll back with: scripts/deploy.sh --env $ENVIRONMENT --rollback"
  fi
  exit 1
fi

log "Deploy complete"
