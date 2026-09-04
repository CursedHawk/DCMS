# The services that get a Vault policy and an AppRole. Sourced, not executed.
#
# ONE list, because two of them diverged and it cost the platform its TLS. apply.sh gained
# `edge` when the edge was built; provision-host.sh kept its own copy and did not. So the
# policy, the AppRole and the dcms-tls-keys transit key all existed, and `--all` quietly
# skipped the one service that needed them -- VAULT_ROLE_ID_EDGE was never written to .env,
# every private key was unreadable, and every TLS handshake on every hostname was refused.
#
# The failure had no symptom of its own: `--all` printed a list of services it had done and
# the missing one simply was not on it. Adding a service here is now the whole change.
DCMS_VAULT_SERVICES="identity admin-api content-api media-worker site-builder site-host ai-gateway email-worker platform-api edge log-janitor"
