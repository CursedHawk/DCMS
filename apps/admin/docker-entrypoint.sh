#!/bin/sh
#
# Substitutes runtime configuration into the built index.html before nginx starts.
#
# The bundle itself is environment-agnostic; only index.html carries the per-environment
# values. That is what lets a single image be promoted from dev to production -- see
# src/runtime-config.ts for why the previous build-time approach could not be.
#
# Installed into /docker-entrypoint.d/, which the nginx image's own entrypoint runs before it
# execs nginx -- so this script does its work and exits; it must NOT exec the CMD itself.
#
# Idempotent by construction: it always rewrites from index.html.template, never from a
# previously substituted index.html, so a container restart does not compound substitutions.

set -eu

ROOT=/usr/share/nginx/html
TEMPLATE="$ROOT/index.html.template"

[ -f "$TEMPLATE" ] || cp "$ROOT/index.html" "$TEMPLATE"

# Defaults keep a bare `docker run` working: same-origin API, and an authority that at least
# resolves to this deployment rather than to a literal placeholder.
: "${DCMS_OIDC_AUTHORITY:=}"
: "${DCMS_OIDC_CLIENT_ID:=dcms-admin-spa}"
: "${DCMS_ADMIN_API_BASE:=/api}"
: "${DCMS_CONTENT_API_BASE:=}"

sed \
  -e "s|__DCMS_OIDC_AUTHORITY__|${DCMS_OIDC_AUTHORITY}|g" \
  -e "s|__DCMS_OIDC_CLIENT_ID__|${DCMS_OIDC_CLIENT_ID}|g" \
  -e "s|__DCMS_ADMIN_API_BASE__|${DCMS_ADMIN_API_BASE}|g" \
  -e "s|__DCMS_CONTENT_API_BASE__|${DCMS_CONTENT_API_BASE}|g" \
  "$TEMPLATE" > "$ROOT/index.html"

# An empty authority means nothing substituted it, and the SPA would fall back to a localhost
# default that cannot work in a deployed environment. Fail loudly instead of serving a login
# button that silently goes nowhere.
if [ -z "$DCMS_OIDC_AUTHORITY" ]; then
  echo "admin-spa: WARNING: DCMS_OIDC_AUTHORITY is not set; the SPA will fall back to its build-time default." >&2
else
  echo "admin-spa: runtime config applied (authority=$DCMS_OIDC_AUTHORITY)."
fi
