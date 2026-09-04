# Policy for the edge (public TLS ingress and reverse proxy).
#
# Read-only on the shared configuration and its own, plus Transit encrypt/decrypt on ONE key.
#
# The narrow Transit grant is the point. The edge holds every tenant domain's TLS private key,
# and it is the most exposed process on the platform -- the only one an anonymous request from
# the internet reaches first. Giving it dcms-tenant-secrets or dcms-social-tokens as well would
# mean a compromise here also decrypted every tenant's AI provider key and Meta OAuth token.
# One key, one purpose, and the blast radius stops at TLS.
#
# There is deliberately no `sign` or `rewrap`, and no capability on the key's own config path:
# the edge uses this key and does not administer it.

path "secret/data/dcms/shared" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/shared" {
  capabilities = ["read"]
}

path "secret/data/dcms/edge" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/edge" {
  capabilities = ["read"]
}

# TLS private keys and the ACME account key, encrypted at rest in edge.certificates and
# edge.acme_accounts. Both directions: the edge writes a key when a certificate is issued and
# reads it back on every cold start.
path "transit/encrypt/dcms-tls-keys" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-tls-keys" {
  capabilities = ["update"]
}
