# Vault server configuration

Everything in this directory is mounted into the Vault container at `/vault/config`, and
Vault merges **every `.hcl` file it finds here** into its server configuration.

That is the mechanism, not an accident: a host enables Transit auto-unseal by copying
`seal-transit.hcl.example` to `seal-transit.hcl`, with no edit to the shared `config.hcl`.

Two consequences worth knowing before adding a file:

- **Only `.hcl` and `.json` are read.** The `.example` suffix is what keeps the template inert.
- **Subdirectories are not recursed**, which is why `../policies/` lives outside this directory.
  Those are Vault *policies* applied with `vault policy write` by `../apply.sh`; they are not
  server configuration, and a policy file landing in here would be a confusing failure.

Nothing else belongs in this directory. `apply.sh` and `init.sh` are one directory up
precisely so they can never be parsed as server config.
