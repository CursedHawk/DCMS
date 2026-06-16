# Production Vault server config (non-dev). File storage + a TCP listener.
# The operator must `vault operator init` + `unseal` on first boot and provision
# the KV/Transit paths and per-service AppRoles (see infra/vault/init.sh for the
# secret layout to recreate). No dev root token is present in this mode.
ui = true

# This host has no swap and Vault runs as a non-root uid (it writes to a
# bind-mounted data dir owned by the deploy user), so it cannot mlock memory.
# With zero swap there is nothing for secrets to leak into, so disabling mlock
# is safe here and avoids a non-root CAP_IPC_LOCK failure at startup.
disable_mlock = true

storage "file" {
  path = "/vault/data"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = "true" # TLS is terminated at Caddy; Vault is on the internal network only.
}

# Default lease/max TTLs; tune per deployment.
default_lease_ttl = "168h"
max_lease_ttl     = "720h"
