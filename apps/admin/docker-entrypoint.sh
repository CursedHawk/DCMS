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

# Defaults keep a bare `docker run` working: the API is same-origin behind the edge.
#
# There is no OIDC authority or client id here any more. The console is signed in by the edge
# (ADR 0014) and holds no tokens, so it needs to know nothing about the identity server --
# which also means one fewer value that can be wrong in a way only a login reveals.
: "${DCMS_ADMIN_API_BASE:=/api}"
: "${DCMS_CONTENT_API_BASE:=}"

sed \
  -e "s|__DCMS_ADMIN_API_BASE__|${DCMS_ADMIN_API_BASE}|g" \
  -e "s|__DCMS_CONTENT_API_BASE__|${DCMS_CONTENT_API_BASE}|g" \
  "$TEMPLATE" > "$ROOT/index.html"

echo "admin-spa: runtime config applied (adminApiBase=$DCMS_ADMIN_API_BASE)."
