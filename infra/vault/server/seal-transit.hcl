# Transit auto-unseal. Vault merges every .hcl in this directory, so simply being here makes
# this active for any host that runs with -config=/vault/config -- which is both dev and
# production, since they share the prod compose overlay. Local development is unaffected: the
# base compose runs Vault in -dev mode and never reads this directory.
#
# ---------------------------------------------------------------------------
# What is here, and what is NOT
# ---------------------------------------------------------------------------
# Everything below is identical for dev and production. The two things that differ come from
# the environment, because this file is committed and deployed to both:
#
#   VAULT_TRANSIT_SEAL_KEY_NAME   dcms-unseal-dev | dcms-unseal-prod
#   VAULT_TRANSIT_SEAL_TOKEN      that environment's transit token
#
# Both are set on the vault service in docker-compose.prod.yml from the host's .env.
#
# Note the asymmetry, which cost an hour to discover: `key_name` and `mount_path` have
# VAULT_TRANSIT_SEAL_* overrides, but `address` does NOT -- it falls back to VAULT_ADDR and
# silently used 127.0.0.1 when a VAULT_TRANSIT_SEAL_ADDRESS was set. So the address lives
# here, in the file, where it cannot be quietly ignored.
#
# ---------------------------------------------------------------------------
# The trade, stated plainly
# ---------------------------------------------------------------------------
# With Shamir, a reboot leaves Vault SEALED. Every service reads its configuration from Vault
# at startup and refuses to start on the 503, so the platform stays down until a human fetches
# a key. "Everything is automatically deployable" is not true of a platform that cannot come
# back from a power cycle without a person.
#
# The chain terminates on VPSM, in a root-only key file opened at boot by
# vault-seal-unseal.service. So root on VPSM yields the unseal capability for BOTH
# environments. Do not read that as "the seal lives on the quiet box": VPSM also publishes
# GitLab on 80/443, and that GitLab deploys to production -- root there already meant shipping
# arbitrary code to prod. This widens an existing boundary rather than opening a new one, from
# "future deploys" to "unwrap both Vaults, including from a stolen backup".
#
# It also introduces an AVAILABILITY COUPLING that did not exist before. This Vault can no
# longer start unless the seal Vault is reachable and unsealed. VPSM has run out of memory and
# stopped serving within the lifetime of this file, so that is not theoretical.
#
# ---------------------------------------------------------------------------
# Migrating an already-initialised Vault from Shamir to this
# ---------------------------------------------------------------------------
# Not automatic. With this file present, restart Vault and then:
#
#   vault operator unseal -migrate    # threshold times, with the Shamir keys
#
# After migration the Shamir keys become RECOVERY keys. Keep them, and keep them OFF both the
# seal host and the serving host: they are what recovers a Vault whose seal key is lost, and
# storing them next to the thing they recover from defeats the point.

seal "transit" {
  # The seal Vault on VPSM, over the private Tailscale mesh -- never the public internet, and
  # not through the reverse proxy that serves GitLab on that same host.
  address         = "https://100.70.75.26:8200"
  mount_path      = "transit/"

  # Its listener uses a self-signed CA, so the CA certificate ships beside this file. A
  # certificate is public by definition; there is no secret here. Vault reads only .hcl and
  # .json from this directory, so the .crt is inert as far as server config is concerned.
  tls_ca_cert     = "/vault/config/seal-ca.crt"

  # Renew the seal token automatically. Without this it expires on its period and the NEXT
  # reboot fails to unseal -- the failure this whole file exists to remove, returning months
  # later looking like something else.
  disable_renewal = "false"
}
