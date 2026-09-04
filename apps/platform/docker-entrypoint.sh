#!/bin/sh
#
# Substitutes runtime configuration into the built index.html before nginx starts.
#
# The bundle is environment-agnostic; only index.html carries the per-environment values. That
# is what lets one image be promoted from dev to production — see src/runtime-config.ts.
#
# Installed into /docker-entrypoint.d/, which the nginx image's own entrypoint runs before it
# execs nginx, so this does its work and exits; it must NOT exec the CMD itself.
#
# Idempotent by construction: it always rewrites from index.html.template, never from a
# previously substituted index.html, so a restart does not compound substitutions.

set -eu

ROOT=/usr/share/nginx/html
TEMPLATE="$ROOT/index.html.template"

[ -f "$TEMPLATE" ] || cp "$ROOT/index.html" "$TEMPLATE"

# Defaults keep a bare `docker run` working. The three API bases are same-origin paths because
# The edge routes all three services under this host by prefix.
: "${DCMS_OIDC_AUTHORITY:=}"
: "${DCMS_OIDC_CLIENT_ID:=dcms-platform-spa}"
: "${DCMS_PLATFORM_API_BASE:=/api/platform}"
: "${DCMS_IDENTITY_API_BASE:=/api/identity}"
: "${DCMS_ADMIN_API_BASE:=/api}"
: "${DCMS_ADMIN_BASE:=}"
: "${DCMS_GRAFANA_BASE:=}"
: "${DCMS_ENVIRONMENT_NAME:=development}"

sed \
  -e "s|__DCMS_OIDC_AUTHORITY__|${DCMS_OIDC_AUTHORITY}|g" \
  -e "s|__DCMS_OIDC_CLIENT_ID__|${DCMS_OIDC_CLIENT_ID}|g" \
  -e "s|__DCMS_PLATFORM_API_BASE__|${DCMS_PLATFORM_API_BASE}|g" \
  -e "s|__DCMS_IDENTITY_API_BASE__|${DCMS_IDENTITY_API_BASE}|g" \
  -e "s|__DCMS_ADMIN_API_BASE__|${DCMS_ADMIN_API_BASE}|g" \
  -e "s|__DCMS_ADMIN_BASE__|${DCMS_ADMIN_BASE}|g" \
  -e "s|__DCMS_GRAFANA_BASE__|${DCMS_GRAFANA_BASE}|g" \
  -e "s|__DCMS_ENVIRONMENT_NAME__|${DCMS_ENVIRONMENT_NAME}|g" \
  "$TEMPLATE" > "$ROOT/index.html"

if [ -z "$DCMS_OIDC_AUTHORITY" ]; then
  echo "platform-spa: WARNING: DCMS_OIDC_AUTHORITY is not set; sign-in will fall back to the build-time default." >&2
fi

# Louder than the others on purpose. This console can suspend a tenant, and the environment
# band is the operator's only signal for which platform they are about to do it to — an
# unset value would silently label production as development.
echo "platform-spa: runtime config applied (environment=$DCMS_ENVIRONMENT_NAME, authority=$DCMS_OIDC_AUTHORITY)."
