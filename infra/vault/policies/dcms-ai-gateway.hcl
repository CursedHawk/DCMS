# Policy for the ai-gateway service.
#
# Read-only, and scoped to exactly two paths: the shared configuration every service needs
# and this service's own. That scoping is the point of having a policy per service at all --
# the previous deployment gave every service one token with read on secret/data/dcms/*, so a
# compromise of any single container exposed every secret the platform holds, including the
# SMTP relay password and the Forgejo admin token.
#
# Generated shape; extras (Transit) are added explicitly below where a service needs them.

path "secret/data/dcms/shared" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/shared" {
  capabilities = ["read"]
}

path "secret/data/dcms/ai-gateway" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/ai-gateway" {
  capabilities = ["read"]
}

# ai-gateway decrypts a tenant's provider key at the moment it proxies a request, and records
# an audit event for every use (AuditActions.SecretAccessed). It has no encrypt capability:
# it never stores a key, so being able to write ciphertext would only widen the blast radius.
path "transit/decrypt/dcms-tenant-secrets" {
  capabilities = ["update"]
}
