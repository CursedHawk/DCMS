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

# Tenant Meta OAuth tokens (social.meta_connections). admin-api gets BOTH directions here,
# which is a deliberate exception to the split above, so it is worth saying why rather than
# leaving it to look like an oversight.
#
# The split for AI keys works because two different services want the two directions: admin-api
# takes the key in, ai-gateway spends it. Nothing like that is true here -- admin-api is the
# service that calls the Graph API, on a background timer with no request in sight, so whoever
# holds decrypt IS the admin plane. The alternative was a whole new service to hold one
# credential, which buys a container and a deploy surface rather than a security boundary.
#
# What the separate key does buy is containment: this grant reaches Meta tokens and nothing
# else. content-api, the public-facing service, is granted neither direction on either key --
# it reaches stories through an internal admin-api endpoint instead, precisely so a token never
# has to be decryptable by the service exposed to the internet.
path "transit/encrypt/dcms-social-tokens" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-social-tokens" {
  capabilities = ["update"]
}

# Uploaded TLS private keys (edge.certificates, Source = Custom). admin-api is where a tenant
# uploads their own certificate, so it needs encrypt.
#
# Encrypt ONLY, and the reasoning is the AI-key split rather than the Meta exception: two
# different services want the two directions here. admin-api takes the key in; the EDGE spends
# it, at handshake time, and it is the only thing that ever reads one back. So a compromise of
# the admin plane -- the broadest HTTP surface on the platform and the most privileged users --
# cannot turn the stored ciphertext into a key that impersonates a tenant's site.
path "transit/encrypt/dcms-tls-keys" {
  capabilities = ["update"]
}
