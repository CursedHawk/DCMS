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

  # Lets Alloy scrape /v1/sys/metrics without a token. Vault's telemetry reports seal
  # state, request rates and lease counts — operational facts, no secret material — and
  # Vault publishes no host port, so the compose network is the only caller.
  #
  # Seal state is the reason this is here at all. This host has no auto-unseal: a reboot
  # leaves Vault sealed and the platform down until someone runs unseal-vault.sh by hand.
  # An alert on vault_core_unsealed == 0 is the difference between finding that out from a
  # dashboard and finding it out from a user.
  telemetry {
    unauthenticated_metrics_access = true
  }
}

telemetry {
  prometheus_retention_time = "30s"
  disable_hostname          = true
}

# Default lease/max TTLs; tune per deployment.
default_lease_ttl = "168h"
max_lease_ttl     = "720h"
