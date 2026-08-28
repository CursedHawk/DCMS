# Vault server configuration

Everything in this directory is mounted into the Vault container at `/vault/config`, and
Vault merges **every `.hcl` file it finds here** into its server configuration.

That is the mechanism, not an accident: `seal-transit.hcl` configures Transit auto-unseal
without a single edit to the shared `config.hcl`, and `seal-ca.crt` sits beside it because
the seal Vault presents a self-signed certificate.

`seal-transit.hcl` is committed and applies to **both** dev and production — everything in it
is identical for the two. The one value that differs, the Transit key name, comes from
`VAULT_TRANSIT_SEAL_KEY_NAME` in the host's `.env`.

Two consequences worth knowing before adding a file:

- **Only `.hcl` and `.json` are read.** That is why `seal-ca.crt` can live here safely: Vault
  ignores it as server configuration, while the seal stanza references it by path.
- **Subdirectories are not recursed**, which is why `../policies/` lives outside this directory.
  Those are Vault *policies* applied with `vault policy write` by `../apply.sh`; they are not
  server configuration, and a policy file landing in here would be a confusing failure.

Nothing else belongs in this directory. `apply.sh` and `init.sh` are one directory up
precisely so they can never be parsed as server config.
