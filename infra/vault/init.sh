#!/bin/sh
# Dev-mode Vault seed: KV v2 paths per service, transit key for tenant secrets.
# Prod uses real Vault with AppRole per service — see docs/runbook.md (Phase 12).
# The prod compose profile must refuse VAULT_DEV_ROOT_TOKEN_ID.
set -e

export VAULT_ADDR="${VAULT_ADDR:-http://vault:8200}"

for i in $(seq 1 30); do
  if vault status >/dev/null 2>&1; then
    break
  fi
  echo "waiting for vault ($i)..."
  sleep 1
done

# KV v2 is mounted at secret/ by default in dev mode.
vault kv put secret/dcms/shared placeholder=true
vault kv put secret/dcms/identity placeholder=true
# admin-api holds the same platform DB password: it is the schema owner, and the only thing
# that can ALTER the role platform-api logs in as. Not an escalation -- admin-api already
# connects with a strictly greater credential.
vault kv put secret/dcms/admin-api \
  placeholder=true \
  Platform__DbPassword="dcms-platform-dev"
vault kv put secret/dcms/content-api placeholder=true
vault kv put secret/dcms/media-worker placeholder=true
vault kv put secret/dcms/site-builder placeholder=true
vault kv put secret/dcms/site-host placeholder=true
vault kv put secret/dcms/ai-gateway placeholder=true

# The platform console. Unlike the placeholders above these carry real (development) values,
# because the services that read them have no other source: the connection string and the
# janitor token were removed from the compose files so Vault is the single place they live.
#
# The two halves of the platform DB password are written together here for the same reason
# `apply.sh --seed` writes them together in production -- admin-api ALTERs the role to this
# value, platform-api connects with it, and neither can read the other's path.
vault kv put secret/dcms/platform-api \
  ConnectionStrings__Postgres="Host=postgres;Port=5432;Database=dcms;Username=dcms_platform;Password=dcms-platform-dev" \
  Observability__LogJanitorSecret="dcms-log-janitor-dev"

vault kv put secret/dcms/log-janitor \
  LOG_JANITOR_SECRET="dcms-log-janitor-dev"

# Global AI defaults. Stored under the ai-gateway service path with "__" section
# separators so the Vault config provider maps them to Ai:Defaults:* in config.
# Set Ai__Defaults__ApiKey here in a real deployment (left empty in dev).
vault kv put secret/dcms/ai-gateway \
  Ai__Defaults__Provider=anthropic \
  Ai__Defaults__Model=claude-opus-4-8 \
  Ai__Defaults__CheapModel=claude-haiku-4-5

# Transit engine for tenant AI keys + visitor JWT key derivation.
if ! vault secrets list | grep -q '^transit/'; then
  vault secrets enable transit
fi
if ! vault read transit/keys/dcms-tenant-secrets >/dev/null 2>&1; then
  vault write -f transit/keys/dcms-tenant-secrets
fi

echo "Vault provisioning complete."
