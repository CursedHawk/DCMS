# Policy for the admin-api service.
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

path "secret/data/dcms/admin-api" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/admin-api" {
  capabilities = ["read"]
}

# Tenant AI provider keys are stored as Transit ciphertext in Postgres. admin-api is where a
# tenant enters one, so it needs encrypt.
#
# It deliberately does NOT get decrypt. Only ai-gateway reads these keys back, at the moment
# it calls the provider. Splitting the two directions means a compromise of the admin plane --
# the one with the broadest HTTP surface and the most privileged users -- cannot turn the
# stored ciphertext back into usable provider credentials.
path "transit/encrypt/dcms-tenant-secrets" {
  capabilities = ["update"]
}
