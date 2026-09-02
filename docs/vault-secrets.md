# What goes in Vault, and where

The inventory of every secret DCMS uses: which Vault path holds it, which services read it,
and which ones **cannot** move to Vault and why.

**Status on vps1 (dev): done.** Every value in Group A below is in Vault and has been removed
from `.env`. The compose overlays no longer carry a single application secret — a service gets
its configuration from its own Vault path or it does not start. Production inherits this by
construction, because it deploys the same three compose files.

## How a service sees Vault

`AddDcmsServiceDefaults` calls `AddDcmsVault(serviceName)`, which loads two KV v2 paths into
`IConfiguration`, in this order:

```
secret/dcms/shared        → every service
secret/dcms/<service>     → that service only
```

A key is written with `__` where the configuration path has `:` — `Audit__ChainKey` becomes
`Audit:ChainKey`.

**Vault is added last, so it wins over environment variables.** That is what made the
migration reversible at every step: a value could be written into Vault while the same value
was still in `.env`, with no behavioural change, and only then removed from `.env`. It is also
the sharp edge — an empty or wrong value in Vault overrides a correct one in the environment,
so it silently takes precedence rather than silently being ignored.

Each service authenticates with its own AppRole and can read **only those two paths**. Verify
after any change:

```bash
vault policy read dcms-<service>
```

## The split that matters

Not everything can live in Vault, and the reason is structural rather than a matter of effort.

**Postgres, MinIO, NATS, Grafana and Caddy cannot read Vault.** They take plain environment
variables, so their credentials must exist in the host's `.env` for those containers to start
at all. Writing the same value into Vault as well does not remove it from `.env` — it creates a
second copy to rotate, and a rotation that updates one and not the other fails in a way that
looks like a corrupt password.

So a secret moves to Vault when **no infrastructure container needs it**. The rest waits for
Vault Agent to render `.env` fragments from Vault, which is what actually removes the
duplication rather than hiding it.

The Forgejo tokens are the case worth understanding, because the obvious classification is
wrong. `FORGEJO_TOKEN`, `FORGEJO_ADMIN_TOKEN` and `FORGEJO_WEBHOOK_SECRET` look like Forgejo's
credentials and were originally filed as such. They are not: they are tokens minted *inside*
Forgejo and *presented to* it by identity and admin-api. The forgejo container's own
environment is `USER_UID` and `USER_GID`. So they moved.

---

## Group A — in Vault, and only in Vault

### `secret/dcms/identity`

| Key | What it is |
|---|---|
| `Audit__ChainKey` | HMAC key for the audit hash chain |
| `Identity__SigningCertificate` | base64 PKCS#12, signs every token |
| `Identity__EncryptionCertificate` | base64 PKCS#12 |
| `Identity__CertificatePassword` | absent — these were exported with no password, and `X509CertificateLoader` treats absent and empty alike |
| `Identity__AdminApiService__Secret` | must equal admin-api's `ServiceClient__ClientSecret` |
| `Identity__SuperAdmin__Password` | first-boot seed only |
| `Authentication__Google__ClientId` / `__ClientSecret` | Google SSO; omit both to hide the button |
| `Forgejo__AdminToken` | provisions a Forgejo user per DCMS user |

Rotating the **signing** certificate invalidates every token signed with the old one. Configure
the new one alongside the old before removing the old, or do it in a maintenance window.

`Identity__SuperAdmin__Email` is deliberately **not** here. It is not a secret, and admin-api
reads the same value as its default alert recipient — a value in two places is a value that
drifts. It stays in `.env` as `SUPERADMIN_EMAIL`.

To confirm identity is really signing with the certificate in Vault rather than a cached copy,
compare the published JWKS `x5t` against the certificate's SHA-1 thumbprint:

```bash
vault kv get -field=Identity__SigningCertificate secret/dcms/identity | base64 -d > /tmp/s.pfx
openssl pkcs12 -in /tmp/s.pfx -clcerts -nokeys -passin pass: -nodes \
  | openssl x509 -outform DER | openssl dgst -sha1 -binary | base64 | tr '+/' '-_' | tr -d '='
curl -s "$PUBLIC_BASE_URL/.well-known/jwks" | grep x5t
rm -f /tmp/s.pfx
```

### `secret/dcms/admin-api`

| Key | What it is |
|---|---|
| `Audit__ChainKey` | HMAC key for the audit hash chain |
| `ServiceClient__ClientSecret` | the secret admin-api presents to identity for client-credentials |
| `Forgejo__Token`, `Forgejo__AdminToken`, `Forgejo__WebhookSecret` | git server API access |
| `Social__Meta__AppId`, `Social__Meta__AppSecret` | the Meta app behind Facebook Login for Business — Pages, and the Instagram accounts linked to them |
| `Social__Instagram__AppId`, `Social__Instagram__AppSecret` | the Instagram Login app, for professional accounts with no Facebook Page |
| `Social__RedirectUri` | the OAuth callback, registered verbatim in the Meta app — `https://<admin host>/api/admin/social/callback` |

**`Audit__ChainKey` must be stable forever.** Rotating it makes every previously written audit
record fail verification — the chain cannot be re-linked, and `audit verify` reports tampering
that never happened.

`Forgejo__WebhookSecret` no longer has a compose default. It used to fall back to a value
published in this repository, so a host that never set it verified push webhooks against a
secret anyone could read. Absent, admin-api registers no webhook rather than a forgeable one.

**The `Social__*` keys are optional, and their absence is a feature.** With no app id and
secret, `MetaSocialOptions.IsConfigured` is false, the connect endpoint answers 501 and the
admin UI hides the button — the same shape as Google SSO in identity, so a dev machine needs no
Meta app at all. Set only the pair you have: an installation with a Facebook app and no
Instagram Login app offers the Facebook path and nothing else.

One redirect URI serves every tenant. Which tenant a callback belongs to comes from the
single-use `social.meta_oauth_states` row named by the `state` parameter, not from the URL —
the callback is necessarily anonymous, because Meta redirects a browser to it with no bearer
token.

### `secret/dcms/content-api`

| Key | What it is |
|---|---|
| `Visitor__SigningKey` | HMAC key for visitor session tokens |
| `Audit__ChainKey` | same value as admin-api's |
| `ServiceClient__ClientSecret` | same value as admin-api's; content-api validates platform tokens |

`Visitor__SigningKey` has never had any source but Vault: it appears in no compose file and no
`.env`, and content-api refuses to start in Production when it is unset, the development
default, or under 32 bytes. The tenant binding in a visitor token is its audience, and a tenant
id is not a secret — so an unconfigured key makes every visitor session on every tenant
forgeable by anyone who has read this repository.

Rotating it invalidates every live visitor session. Nothing else breaks.

### `secret/dcms/email-worker`

| Key | What it is |
|---|---|
| `Email__Host`, `Email__Port`, `Email__User`, `Email__Password`, `Email__UseStartTls` | SMTP relay |
| `Email__FromAddress`, `Email__FromName` | envelope sender |

email-worker is the only service that speaks SMTP — everything else publishes to the EMAIL
work queue — so these belong in exactly one place, and that place is now a path only its
AppRole can read. Relays that police the envelope sender (iCloud, Gmail) reject a
`FromAddress` that is not the authenticated account or a verified alias, with a 5xx that
email-worker treats as permanently undeliverable.

With the path empty the service falls back to `appsettings`, which targets the in-cluster
Mailpit: mail is captured rather than delivered. That is the safe direction to fail but not an
obvious one, so check this path before blaming the relay.

### `secret/dcms/media-worker`, `secret/dcms/site-host`

| Key | What it is |
|---|---|
| `Audit__ChainKey` | same value as admin-api's |

### `secret/dcms/ai-gateway`

| Key | What it is |
|---|---|
| `Ai__Defaults__Provider`, `Ai__Defaults__Model`, `Ai__Defaults__CheapModel` | not secrets, just configuration that differs per environment |
| `Audit__ChainKey` | same value as admin-api's |

Tenant AI provider keys are **not** stored here. They live encrypted in the database, wrapped
with `transit/keys/dcms-tenant-secrets`, which is why admin-api holds encrypt and ai-gateway
holds decrypt and neither holds both.

### `secret/dcms/site-builder`

Nothing. Its Postgres and MinIO credentials are Group B, and it deliberately holds no audit
chain key.

### `secret/dcms/shared`

A `placeholder`, and it should stay that way unless something is genuinely common to every
service. `shared` is the one path that bypasses the per-service split, so anything put there
is handed to all eight roles at once.

### A note on `Audit__ChainKey`

The **same value** goes into six paths: `identity`, `admin-api`, `content-api`, `media-worker`,
`site-host`, `ai-gateway` — every service that writes the audit log directly.

It is deliberately **not** in `secret/dcms/shared`, even though six copies is uglier than one.
`site-builder` and `email-worker` reach the audit log over NATS and must not hold the chain key
at all; `shared` is readable by every AppRole, so putting it there would hand it to both and
quietly undo that separation. Six copies of one value that must never change is a smaller
problem than a key two services should not be able to read.

---

## Group B — stays in `.env`, and why

Each is read by a container that cannot talk to Vault.

| Variable | Needed by |
|---|---|
| `POSTGRES_PASSWORD` | the postgres container itself |
| `SITEBUILDER_DB_PASSWORD`, `RLS_DB_PASSWORD`, `GRAFANA_DB_USER`, `GRAFANA_DB_PASSWORD` | `postgres-bootstrap`, which creates the roles |
| `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `SITEBUILDER_MINIO_USER`, `SITEBUILDER_MINIO_PASSWORD` | the minio container and its init job |
| `NATS_APP_PASSWORD`, `NATS_SYS_PASSWORD` | the nats container (`${VAR:?}` — a missing one fails the whole compose invocation) |
| `GRAFANA_ADMIN_PASSWORD` | the grafana container |
| `GRAFANA_OIDC_CLIENT_SECRET` | grafana **and** identity, which seeds the client — moving it would create a second copy to rotate, not remove one |
| `ALERT_WEBHOOK_SECRET` | grafana **and** admin-api, which compares it; same reason. A mismatch rejects every alert, and a rejected alert looks exactly like having nothing to alert about |
| `ACME_EMAIL` | caddy |
| `VAULT_ROLE_ID_*`, `VAULT_SECRET_ID_*` | the credentials used to reach Vault — necessarily outside it |
| `VAULT_TRANSIT_SEAL_TOKEN`, `VAULT_TRANSIT_SEAL_KEY_NAME` | the vault container's own seal |

The last two rows are not a limitation to fix. A credential that unlocks Vault cannot itself
live in Vault, and every bootstrap chain terminates somewhere.

**Not secrets at all**, though they live in the same file: `DCMS_ENV`, `DCMS_HOST`,
`PUBLIC_BASE_URL`, `ADMIN_HOST`, `GRAFANA_DOMAIN`, `GRAFANA_ROOT_URL`, `GIT_HOST`,
`SUPERADMIN_EMAIL`, `ALERT_RECIPIENTS`, `DCMS_BUILD_RUNTIME`, `IDENTITY_ALLOW_EPHEMERAL_KEYS`.

---

## Transit keys — never recreate these

| Key | Wraps |
|---|---|
| `transit/keys/dcms-tenant-secrets` | tenant AI provider keys, stored encrypted in the database |
| `transit/keys/dcms-dataprotection` | the ASP.NET Data Protection key ring, when `DataProtection:ProtectWithTransit` is on |
| `transit/keys/dcms-social-tokens` | tenant Meta (Facebook/Instagram) OAuth tokens in `social.meta_connections` |

A new key of the same name **cannot decrypt existing ciphertext**. Recreating
`dcms-tenant-secrets` makes every tenant's stored provider key unreadable; recreating
`dcms-dataprotection` invalidates the key ring, which logs everyone out and strands the Forgejo
sync outbox; recreating `dcms-social-tokens` makes every stored Meta connection unreadable and
every tenant has to reconnect their account by hand. `infra/vault/apply.sh` uses
`vault write -f`, which is a no-op on an existing key — that is deliberate, not laziness.

`dcms-social-tokens` is the one key a single service holds **both** directions on, which is
worth explaining rather than leaving to look like an oversight. The encrypt/decrypt split works
for AI keys because two different services want the two halves — admin-api takes the key in,
ai-gateway spends it. Nothing like that is true here: admin-api is the service that calls the
Graph API, on a background timer with no request in sight, so whoever holds decrypt *is* the
admin plane. What the separate key buys is containment — this grant reaches Meta tokens and
nothing else. content-api is granted neither direction on either key; it reads live Instagram
stories through an internal admin-api endpoint precisely so a tenant credential never has to be
decryptable by the service exposed to the internet.

---

## Operator access

Nothing routine uses the root token. `infra/vault/policies/dcms-ops.hcl` covers everything
`apply.sh` does and everything writing a secret value needs, and it is attached to the
`dcms-ops` AppRole whose credentials live in `~/.dcms/vault-ops.env` on the host.

```bash
cd infra/vault
export VAULT_ADDR=http://127.0.0.1:8200
export PATH="$PWD/bin:$PATH"          # a `vault` that execs into the container
set -a; . ~/.dcms/vault-ops.env; set +a
export VAULT_TOKEN=$(vault write -field=token auth/approle/login \
  role_id=$VAULT_OPS_ROLE_ID secret_id=$VAULT_OPS_SECRET_ID)
./apply.sh --check
```

No host installs the Vault binary — `infra/vault/bin/vault` execs into the running container,
which is also why `apply.sh` pipes every policy on stdin rather than naming a file: a filename
would be resolved inside the container, where this directory is not mounted.

The policy denies writing `sys/policies/acl/dcms-ops`, so an ops credential cannot grant
itself more. `apply.sh` therefore reports and skips that one line when run as ops, and applies
everything after it — changing what an operator may do is not an operator-level change.

The root token is **not** revoked. On Vault 2.0.4 `sys/generate-root/attempt` returns 403
without a root token (verified with an ops token, an invalid token, and no token header at
all), so the recovery key cannot mint a replacement and revoking is irreversible. See
`~/.dcms/README` on either host.

## Writing and checking values

```bash
export VAULT_ADDR=... VAULT_TOKEN=<admin>
vault kv put secret/dcms/content-api Visitor__SigningKey="$(openssl rand -base64 32)"
```

`vault kv put` **replaces the whole secret**. To add one key without dropping the others use
`vault kv patch`, or you will silently delete the rest of the path. Values given on the command
line also land in shell history and in `ps`; for anything long-lived, pipe JSON on stdin
instead:

```bash
echo '{"Audit__ChainKey":"..."}' | vault kv patch -mount=secret dcms/admin-api -
```

Before a deploy:

```bash
VAULT_ADDR=... VAULT_TOKEN=<admin> infra/vault/apply.sh --check
```

It asserts presence, never reads a value, and exits non-zero on anything missing. `required_keys_for()`
in that script is the list, and it has to keep tracking this document: a path missing a key is
now a service that does not start, so the assertion is what turns that into a deploy that stops
before anything rolls.

## Adding a service, or a key to an existing one

1. Write the value into the service's own path. Never `shared` unless every service needs it.
2. Add it to `required_keys_for()` in `infra/vault/apply.sh`.
3. If the value is currently also in a compose overlay, deploy once with both present — Vault
   wins, so this is a no-op — and only then remove the compose line and the `.env` line.
   Both sources present is a safe intermediate state; Vault-only with a typo is not.
4. Verify with the AppRole itself, not with a root token. A root token proves the value is
   there; it does not prove the service can read it, and the policy is the half that gets
   forgotten.

```bash
T=$(vault write -field=token auth/approle/login \
      role_id=$VAULT_ROLE_ID_X secret_id=$VAULT_SECRET_ID_X)
VAULT_TOKEN=$T vault kv get secret/dcms/<service>
```
