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
  tls_disable = "true" # TLS is terminated at the edge; Vault is on the internal network only.

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
  # This is the window a metric survives in Vault's in-memory Prometheus sink, and at 30s it
  # was SHORTER THAN THE SCRAPE INTERVAL. Alloy's `infra` job runs on the default 60s, so
  # every timer and counter Vault publishes -- vault_core_handle_request and its _count,
  # which are the request rate and latency the storage/edge dashboard graphs -- had expired
  # before anything came to read them. The two gauges that survived (vault_core_unsealed,
  # vault_core_active) are emitted fresh on every request to the endpoint, which is why the
  # seal-state panel worked and made the rest look like Vault simply being idle.
  #
  # Ten minutes is comfortably past any scrape interval this stack is likely to use. The
  # cost is bounded: it retains a fixed, small set of Vault's own metrics in memory, and
  # Vault's own default is 24 hours.
  prometheus_retention_time = "10m"
  disable_hostname          = true
}

# Default lease/max TTLs; tune per deployment.
default_lease_ttl = "168h"
max_lease_ttl     = "720h"
