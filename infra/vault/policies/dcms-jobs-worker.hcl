# Policy for the jobs-worker service (ADR 0036): the platform's background work -- job lanes,
# plugin workers, event consumers, outboxes and sweeps. It used to run on admin-api's AppRole
# because it was admin-api's image in another role; it is its own service now, so it reads its
# own path. apply.sh mirrors the values it needs there from secret/dcms/admin-api (see
# mirror_worker_secrets), so an operator still writes each secret once.
#
# Read-only, and scoped to the shared configuration and this service's own path.

path "secret/data/dcms/shared" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/shared" {
  capabilities = ["read"]
}

path "secret/data/dcms/jobs-worker" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/jobs-worker" {
  capabilities = ["read"]
}

# Both directions on the keys whose ciphertext background work spends: Meta tokens (feed sync,
# token refresh), plugin instance credentials (plugin jobs and event handlers), and external API
# connection credentials (the snapshot refresh). The reasons each is both-directions are in
# dcms-admin-api.hcl; they are the same here because this is where that code runs.
#
# Deliberately absent: dcms-tenant-secrets and dcms-tls-keys. Only admin-api's request
# endpoints encrypt with those (a tenant entering an AI key, uploading a certificate); nothing in
# the background touches them.
path "transit/encrypt/dcms-social-tokens" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-social-tokens" {
  capabilities = ["update"]
}

path "transit/encrypt/dcms-plugin-secrets" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-plugin-secrets" {
  capabilities = ["update"]
}

path "transit/encrypt/dcms-api-connections" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-api-connections" {
  capabilities = ["update"]
}

# The shared Data Protection key ring. Both directions, as for every service sharing the ring:
# a Google Drive import token protected by admin-api's endpoint is unprotected here by the job.
path "transit/encrypt/dcms-dataprotection" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-dataprotection" {
  capabilities = ["update"]
}
