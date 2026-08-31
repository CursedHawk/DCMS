# Policy for operator and CI access to a DCMS Vault.
#
# It exists so the root token can be revoked. Root is unrevokable, never expires, bypasses
# every policy including the audit device, and on both hosts it was sitting in a plaintext
# file owned by the deploy user -- next to the data it protects. This policy covers everything
# infra/vault/apply.sh does and everything writing a secret value needs, so nothing routine
# requires root any more.
#
# The remaining path to full admin is `vault operator generate-root` with the recovery keys,
# which is what recovery keys are for: an offline credential, used rarely, in the open.
#
# What is deliberately absent matters as much as what is here:
#
#   sys/seal            an ops credential that can seal Vault can stop the whole platform,
#                       and every service reads its configuration from Vault at startup
#   sys/audit/*         a credential that can disable auditing can act without a record;
#                       the seal Vault's audit log is what diagnosed the transit 403
#   sys/generate-root   escalation back to the thing this policy replaces
#   sys/policies/acl/dcms-ops  self-escalation -- see the explicit deny below
#   auth/token/create-orphan   a token outliving the one that made it

# ---- secret values -------------------------------------------------------
# Full CRUD under the DCMS tree only. Nothing outside secret/dcms/ is reachable.
path "secret/data/dcms/*" {
  capabilities = ["create", "read", "update", "patch", "delete", "list"]
}

path "secret/metadata/dcms/*" {
  capabilities = ["create", "read", "update", "delete", "list"]
}

# `vault kv list secret/dcms` and the mount lookup the kv helper does first.
path "secret/metadata/dcms" {
  capabilities = ["list", "read"]
}

path "sys/internal/ui/mounts/*" {
  capabilities = ["read"]
}

# ---- transit -------------------------------------------------------------
# Create and rotate, never delete. Deleting dcms-tenant-secrets makes every tenant's stored
# provider key unreadable and deleting dcms-dataprotection strands the Forgejo outbox; neither
# is recoverable, and neither is something an ops credential should be able to do by accident.
path "transit/keys/*" {
  capabilities = ["create", "read", "update", "list"]
}

path "transit/encrypt/*" {
  capabilities = ["update"]
}

path "transit/decrypt/*" {
  capabilities = ["update"]
}

# ---- policies and roles --------------------------------------------------
path "sys/policies/acl/dcms-*" {
  capabilities = ["create", "read", "update", "list"]
}

# ...but not its own, which would make every other restriction here advisory.
path "sys/policies/acl/dcms-ops" {
  capabilities = ["deny"]
}

path "auth/approle/role/*" {
  capabilities = ["create", "read", "update", "list"]
}

# ---- mounts --------------------------------------------------------------
# apply.sh is idempotent and enables these only when absent, which is the case exactly once
# per environment -- but "once per environment" includes provisioning production.
path "sys/mounts" {
  capabilities = ["read", "list"]
}

path "sys/mounts/secret" {
  capabilities = ["create", "read", "update"]
}

path "sys/mounts/transit" {
  capabilities = ["create", "read", "update"]
}

path "sys/auth" {
  capabilities = ["read", "list"]
}

path "sys/auth/approle" {
  capabilities = ["create", "read", "update", "sudo"]
}

# ---- read-only introspection --------------------------------------------
path "sys/health" {
  capabilities = ["read"]
}

path "sys/seal-status" {
  capabilities = ["read"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}

path "auth/token/renew-self" {
  capabilities = ["update"]
}
