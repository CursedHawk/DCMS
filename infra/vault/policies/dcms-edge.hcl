# Policy for the edge (public TLS ingress and reverse proxy).
#
# Read-only on the shared configuration and its own, plus Transit encrypt/decrypt on two keys,
# each covering exactly one thing the edge stores at rest.
#
# The narrow Transit grant is the point. The edge holds every tenant domain's TLS private key
# and the session cookie key ring, and it is the most exposed process on the platform -- the
# only one an anonymous request from the internet reaches first. Giving it dcms-tenant-secrets,
# dcms-social-tokens or the shared dcms-dataprotection as well would mean a compromise here
# also decrypted every tenant's AI provider key, every Meta OAuth token, or identity's cookies
# and the queued git credentials. One key per purpose, and the blast radius stops there.
#
# There is deliberately no `sign` or `rewrap`, and no capability on either key's own config
# path: the edge uses these keys and does not administer them.

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

# The edge's own Data Protection key ring (edge.data_protection_keys), which protects the
# session cookie the edge asserts to Grafana, Forgejo and the admin API. Both directions: the
# framework writes a key when one rolls and reads the ring back on every cold start.
path "transit/encrypt/dcms-edge-dataprotection" {
  capabilities = ["update"]
}

path "transit/decrypt/dcms-edge-dataprotection" {
  capabilities = ["update"]
}
