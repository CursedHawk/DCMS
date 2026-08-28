# What goes in Vault, and where

The inventory of every secret DCMS uses: which Vault path holds it, which services read it,
and which ones **cannot** move to Vault yet and why.

## How a service sees Vault

`AddDcmsServiceDefaults` calls `AddDcmsVault(serviceName)`, which loads two KV v2 paths into
`IConfiguration`, in this order:

```
secret/dcms/shared        → every service
secret/dcms/<service>     → that service only
```

A key is written with `__` where the configuration path has `:` — `Audit__ChainKey` becomes
`Audit:ChainKey`. Later sources win, so a per-service key overrides the same key in `shared`,
and both override `appsettings.json`.

Each service authenticates with its own AppRole and can read **only those two paths**. Verify
after any change:

```bash
vault policy read dcms-<service>
```

## The split that matters

Not everything can live in Vault today, and the reason is structural rather than a matter of
effort.

**Postgres, MinIO, NATS, Grafana, Forgejo and Caddy cannot read Vault.** They take plain
environment variables, so their credentials must exist in the host's `.env` for those
containers to start at all. Writing the same value into Vault as well does not remove it from
`.env` — it creates a second copy to rotate, and a rotation that updates one and not the other
fails in a way that looks like a corrupt password.

So a secret only moves to Vault when **no infrastructure container needs it**. The rest waits
for Vault Agent to render `.env` fragments from Vault (Part 2 of the deployment plan), which is
what actually removes the duplication rather than hiding it.

---

## Group A — belongs in Vault now (no infra container needs it)

### `secret/dcms/content-api`

| Key | What it is |
|---|---|
| `Visitor__SigningKey` | HMAC key for visitor session tokens. **Already in Vault and in use.** |
| `Audit__ChainKey` | same value as admin-api's |
| `ServiceClient__ClientSecret` | same value as admin-api's; content-api validates platform tokens |

Vault-only by design: it appears in no compose file and no `.env`, and content-api refuses to
start in Production when it is unset, the development default, or under 32 bytes. The tenant
binding in a visitor token is its audience, and a tenant id is not a secret — so an
unconfigured key makes every visitor session on every tenant forgeable by anyone who has read
this repository.

```bash
vault kv put secret/dcms/content-api Visitor__SigningKey="$(openssl rand -base64 32)"
```

Rotating it invalidates every live visitor session. Nothing else breaks.

### `secret/dcms/admin-api`

| Key | What it is |
|---|---|
| `Audit__ChainKey` | HMAC key for the audit hash chain |
| `Alerting__WebhookSecret` | bearer token Grafana presents to `POST /api/internal/alerts` |
| `ServiceClient__ClientSecret` | the secret admin-api presents to identity for client-credentials |
| `Forgejo__Token`, `Forgejo__AdminToken`, `Forgejo__WebhookSecret` | git server API access |

**`Audit__ChainKey` must be stable forever.** Rotating it makes every previously written audit
record fail verification — the chain cannot be re-linked, and `audit verify` reports tampering
that never happened.

`Alerting__WebhookSecret` is also needed by the **Grafana container**, which cannot read Vault,
so it stays in `.env` as well until Vault Agent. It is listed here because admin-api is the
side that compares it; a mismatch rejects every alert, and a rejected alert looks exactly like
having nothing to alert about.

### `secret/dcms/identity`

| Key | What it is |
|---|---|
| `Audit__ChainKey` | same value as admin-api's |
| `Identity__SigningCertificate` | base64 PKCS#12, signs every token |
| `Identity__EncryptionCertificate` | base64 PKCS#12 |
| `Identity__CertificatePassword` | export password, empty if generated with `-passout pass:` |
| `Identity__AdminApiService__Secret` | must equal admin-api's `ServiceClient__ClientSecret` |
| `Identity__SuperAdmin__Email` / `__Password` | first-boot seed only |
| `Authentication__Google__ClientId` / `__ClientSecret` | Google SSO; omit both to hide the button |
| `Forgejo__AdminToken` | provisions a Forgejo user per DCMS user |

Rotating the **signing** certificate invalidates every token signed with the old one. Configure
the new one alongside the old before removing the old, or do it in a maintenance window.

### `secret/dcms/email-worker`

| Key | What it is |
|---|---|
| `Email__Host`, `Email__Port`, `Email__User`, `Email__Password`, `Email__UseStartTls` | SMTP relay |
| `Email__FromAddress`, `Email__FromName` | envelope sender |

email-worker is the only service that speaks SMTP — everything else publishes to the EMAIL
work queue — so these belong in exactly one place. Relays that police the envelope sender
(iCloud, Gmail) reject a `FromAddress` that is not the authenticated account or a verified
alias, with a 5xx that email-worker treats as permanently undeliverable.

**This path does not exist yet.** Create it before moving the values.

### `secret/dcms/media-worker`, `secret/dcms/site-host`

| Key | What it is |
|---|---|
| `Audit__ChainKey` | same value as admin-api's |

### `secret/dcms/site-builder`

Nothing yet. Its Postgres and MinIO credentials are Group B, and it deliberately holds no
audit chain key.

### A note on `Audit__ChainKey`

The **same value** goes into six paths: `identity`, `admin-api`, `content-api`, `media-worker`,
`site-host`, `ai-gateway` — every service that writes the audit log directly, which is exactly
the set that merges the `*prod-env` anchor today.

It is deliberately **not** in `secret/dcms/shared`, even though six copies is uglier than one.
`site-builder` and `email-worker` reach the audit log over NATS and must not hold the chain key
at all; `shared` is readable by every AppRole, so putting it there would hand it to both and
quietly undo that separation. Six copies of one value that must never change is a smaller
problem than a key that two services should not be able to read.

### `secret/dcms/ai-gateway`

| Key | What it is |
|---|---|
| `Ai__Defaults__Provider`, `Ai__Defaults__Model`, `Ai__Defaults__CheapModel` | **already set** — not secrets, just configuration that differs per environment |
| `Audit__ChainKey` | same value as admin-api's |

Tenant AI provider keys are **not** stored here. They live encrypted in the database, wrapped
with `transit/keys/dcms-tenant-secrets`, which is why admin-api holds encrypt and ai-gateway
holds decrypt and neither holds both.

### `secret/dcms/shared`

Currently a `placeholder`. Reserved for values genuinely common to every service. Resist
putting anything here that only some services should hold — the per-service split is the whole
point of the AppRole work, and `shared` is the one path that bypasses it.

---

## Group B — must stay in `.env` until Vault Agent

Each is read by a container that cannot talk to Vault.

| Variable | Needed by |
|---|---|
| `POSTGRES_PASSWORD` | the postgres container itself |
| `SITEBUILDER_DB_PASSWORD`, `RLS_DB_PASSWORD`, `GRAFANA_DB_USER`, `GRAFANA_DB_PASSWORD` | `postgres-bootstrap`, which creates the roles |
| `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `SITEBUILDER_MINIO_USER`, `SITEBUILDER_MINIO_PASSWORD` | the minio container and its init job |
| `NATS_APP_PASSWORD`, `NATS_SYS_PASSWORD` | the nats container (`${VAR:?}` — a missing one fails the whole compose invocation) |
| `GRAFANA_ADMIN_PASSWORD`, `GRAFANA_OIDC_CLIENT_SECRET`, `ALERT_WEBHOOK_SECRET` | the grafana container |
| `ACME_EMAIL` | caddy |
| `FORGEJO_ADMIN_TOKEN`, `FORGEJO_TOKEN`, `FORGEJO_WEBHOOK_SECRET` | forgejo, plus identity and admin-api |
| `VAULT_ROLE_ID_*`, `VAULT_SECRET_ID_*` | the credentials used to reach Vault — necessarily outside it |
| `VAULT_TRANSIT_SEAL_TOKEN`, `VAULT_TRANSIT_SEAL_KEY_NAME` | the vault container's own seal |

The last two rows are not a limitation to fix. A credential that unlocks Vault cannot itself
live in Vault, and every bootstrap chain terminates somewhere.

**Not secrets at all**, though they live in the same file: `DCMS_ENV`, `DCMS_HOST`,
`PUBLIC_BASE_URL`, `ADMIN_HOST`, `GRAFANA_DOMAIN`, `GRAFANA_ROOT_URL`, `GIT_HOST`,
`EMAIL_FROM_*`, `ALERT_RECIPIENTS`, `DCMS_BUILD_RUNTIME`, `IDENTITY_ALLOW_EPHEMERAL_KEYS`.

---

## Transit keys — never recreate these

| Key | Wraps |
|---|---|
| `transit/keys/dcms-tenant-secrets` | tenant AI provider keys, stored encrypted in the database |
| `transit/keys/dcms-dataprotection` | the ASP.NET Data Protection key ring, when `DataProtection:ProtectWithTransit` is on |

A new key of the same name **cannot decrypt existing ciphertext**. Recreating
`dcms-tenant-secrets` makes every tenant's stored provider key unreadable; recreating
`dcms-dataprotection` invalidates the key ring, which logs everyone out and strands the Forgejo
sync outbox. `infra/vault/apply.sh` uses `vault write -f`, which is a no-op on an existing key
— that is deliberate, not laziness.

---

## Writing the values

```bash
export VAULT_ADDR=... VAULT_TOKEN=<admin>

vault kv put secret/dcms/content-api  Visitor__SigningKey="$(openssl rand -base64 32)"
vault kv put secret/dcms/admin-api    Audit__ChainKey="$(openssl rand -base64 32)" ...
```

`vault kv put` **replaces the whole secret**. To add one key without dropping the others use
`vault kv patch`, or you will silently delete the rest of the path.

## Checking before a deploy

```bash
VAULT_ADDR=... VAULT_TOKEN=<admin> infra/vault/apply.sh --check
```

It asserts presence, never reads a value, and exits non-zero on anything missing. It currently
knows about `content-api → Visitor__SigningKey` and `admin-api → Audit__ChainKey`; extend
`required_keys_for()` as values move out of `.env`, so that the assertion grows with the
migration instead of lagging it.

## Order to migrate in

1. Values **only** application services read (Group A) — nothing else has to change.
2. Extend `required_keys_for()` for each one as it lands, so a missing value fails the deploy
   rather than a service.
3. Remove the corresponding line from `.env` **only after** a deploy has proven the service
   reads it from Vault. Both sources present is a safe intermediate state; Vault-only with a
   typo is not.
4. Group B waits for Vault Agent. Moving it earlier duplicates rather than migrates.


---

## Open gap found while writing this

**content-api and ai-gateway run with an ephemeral Data Protection key ring.** Both log
`Storing keys in a directory that may not be persisted outside of the container` and
`No XML encryptor configured` at startup. identity and admin-api are correct — they call
`AddDcmsDataProtection` and persist to `dataprotection.data_protection_keys` — but the other
two were never wired up.

It matters for content-api in particular: it is the public delivery plane, it hosts the chat
hub, and anything ASP.NET protects there (antiforgery among others) is readable only by the
replica that wrote it and only until that container is recreated. It is the same defect Phase B
fixed for identity, in a service that has not been given the fix.

Not fixed here because this document is an inventory, not a change. It belongs with the
statelessness work: `AddDcmsDataProtection` in both services, and `secret/dcms/*` needs no new
key for it — the ring lives in Postgres, and Transit wrapping is already behind
`DataProtection:ProtectWithTransit`.
