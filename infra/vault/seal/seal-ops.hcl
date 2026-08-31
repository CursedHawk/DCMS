# Operator policy for the SEAL Vault on VPSM.
#
# The seal Vault does one job: hold the Transit keys that unwrap dev's and production's master
# keys. That makes it the most consequential box in the platform and the one with the least to
# do -- outside provisioning a new environment, nobody touches it. So the routine credential
# should not be root, and this is what replaces it.
#
# Attached ALONGSIDE dcms-unseal-dev and dcms-unseal-prod on the ops AppRole, not instead of
# them: Vault only lets a token create children whose policies it already holds, and minting
# an environment's seal token is the main thing this credential exists to do.
#
# Deliberately absent:
#
#   sys/seal              sealing THIS Vault takes dev and production down together -- neither
#                         can unseal without it, and every service reads its config from Vault
#                         at startup. It is the single most destructive button here.
#   transit delete/       deleting dcms-unseal-<env> permanently bricks that environment's
#                         Vault: the master key is wrapped with it and nothing else can unwrap.
#   sys/audit/*           the seal Vault's audit log is the only record of unwrap requests, and
#                         it is what diagnosed the transit-seal 403 that took a day to find.
#   sys/generate-root     escalation to the thing this replaces.
#   sys/policies/acl/seal-ops   self-escalation.

# Environment unseal keys: create and inspect, never destroy.
path "transit/keys/dcms-unseal-*" {
  capabilities = ["create", "read", "update", "list"]
}

path "transit/keys" {
  capabilities = ["list"]
}

# Per-environment seal policies, for provisioning a new environment.
path "sys/policies/acl/dcms-unseal-*" {
  capabilities = ["create", "read", "update", "list"]
}

# ...but not its own.
path "sys/policies/acl/seal-ops" {
  capabilities = ["deny"]
}

# Minting a seal token for an environment. The child's policies must be a subset of this
# token's, which is why the AppRole carries the unseal policies too.
#
# `sudo` is here for one specific reason: a seal token must be PERIODIC, and Vault requires
# root or sudo to create a periodic token. A non-periodic one expires, and when an
# environment's seal token expires its Vault cannot unwrap its master key at startup -- so the
# next restart of dev or production simply never comes back. (A max_lease_ttl of 2h once
# capped these silently despite -period=768h, which is the same outage arriving more slowly.)
#
# It is narrower than it looks: sudo lifts the periodic restriction, it does not lift the
# subset rule. This token still cannot create a child with any policy it does not itself hold,
# and it holds exactly seal-ops plus the two unseal policies.
path "auth/token/create" {
  capabilities = ["create", "update", "sudo"]
}

path "auth/token/lookup" {
  capabilities = ["create", "update", "read"]
}

path "auth/token/revoke" {
  capabilities = ["update"]
}

# Revoking by ACCESSOR, which is how you revoke a token you did not keep. Minting an
# environment's seal token and never being able to withdraw it is not a usable arrangement:
# the accessor is the only handle left once the token itself has gone into a host's .env.
path "auth/token/revoke-accessor" {
  capabilities = ["update"]
}

path "auth/token/lookup-accessor" {
  capabilities = ["update"]
}

path "auth/token/lookup-self" {
  capabilities = ["read"]
}

path "auth/token/renew-self" {
  capabilities = ["update"]
}

path "sys/seal-status" {
  capabilities = ["read"]
}

path "sys/health" {
  capabilities = ["read"]
}

path "sys/mounts" {
  capabilities = ["read", "list"]
}
