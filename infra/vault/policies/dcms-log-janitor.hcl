# Policy for the log-janitor sidecar.
#
# The narrowest policy in the platform, for the container with the widest filesystem reach:
# it holds write access to /var/lib/docker/containers, so the one thing it must not also have
# is a broad view of the platform's secrets. It reads ONE key from ONE path — the shared token
# platform-api authenticates to it with — and deliberately not secret/dcms/shared, which every
# other service can read and which this one needs nothing from.

path "secret/data/dcms/log-janitor" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/log-janitor" {
  capabilities = ["read"]
}
