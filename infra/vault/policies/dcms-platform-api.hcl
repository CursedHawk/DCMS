# Policy for the platform console's API.
#
# Read-only on exactly two paths, like every other service. Worth stating what is NOT here,
# because this is the service holding the platform's delete buttons: no transit grant, no
# access to any other service's path, and nothing that would let it read the audit chain key.
# It cannot append to the audit chain — it publishes audit records over NATS and admin-api's
# writer appends them — so a chain key would be a credential it has no use for.

path "secret/data/dcms/platform-api" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/platform-api" {
  capabilities = ["read"]
}

path "secret/data/dcms/shared" {
  capabilities = ["read"]
}

path "secret/metadata/dcms/shared" {
  capabilities = ["read"]
}
