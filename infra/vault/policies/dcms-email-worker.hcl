# Policy for the email-worker service.
#
# NOTE: email-worker does not read Vault today. It deliberately inherits no Vault environment
# (docker-compose.prod.yml gives it only the mail relay settings), because it has no database
# and no object store and the SMTP credentials are the one secret it needs. The role exists so
# that moving those credentials into Vault -- which is the direction of travel -- is a compose
# change rather than a Vault change made under time pressure.
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

path "secret/data/dcms/email-worker" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/email-worker" {
  capabilities = ["read"]
}
