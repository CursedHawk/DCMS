# ADR 0011: DCMS-managed wildcard certificates over DNS-01

**Status:** accepted (2026-09-06) · **Phase:** extends
[ADR 0010](0010-yarp-edge.md) · **Revisit:** when Certes gains ARI support

## Context

`Dcms.Edge` issues one Let's Encrypt certificate **per hostname** over HTTP-01. It
works — on 2026-09-05 all twelve hostnames served correctly — but the model has a
ceiling written into it.

Let's Encrypt allows **50 certificates per registered domain per week**, and every
name this platform serves lives under one registered domain, `highgeek.eu`. Each
provisioned tenant subdomain (`{guid:N}.dcms.highgeek.eu`) spends one of those fifty.
At ~50 new tenants in a week, issuance fails for *everyone*, and the failure is
indistinguishable from the platform being down. ADR 0010 identified this and deferred
it: "Phase 5 adds a DNS-01 wildcard for the managed zone".

Two other things pushed the decision now:

- **Ownership was muddled.** The edge auto-issued for tenant-owned custom domains and
  for the platform's own names through the same path, so a tenant's expired DNS record
  and the platform's own certificates shared one failure budget.
- **Nothing was configurable.** The set of platform hostnames is compiled into
  `EdgeOptions.PlatformHostnames`. Adding a domain the platform should keep renewed
  meant a code change and a deploy.

### What Let's Encrypt requires

Verified 2026-09-06 against `letsencrypt.org/docs` (rate-limit page last updated
2026-08-05):

- **Wildcards are DNS-01 only.** HTTP-01 and TLS-ALPN-01 are refused for a `*.x`
  identifier. The existing issuer is HTTP-01 exclusively, so this is new machinery
  rather than a setting.
- The TXT record goes at `_acme-challenge.<base>`, base being the identifier with
  `*.` stripped — `*.dcms.highgeek.eu` validates at `_acme-challenge.dcms.highgeek.eu`.
- **An order containing both `highgeek.eu` and `*.highgeek.eu` produces two
  authorizations that both write TXT at `_acme-challenge.highgeek.eu`, with different
  values, both present simultaneously.** Records must be *added*, never replaced.
- Rate limits: 50 certificates per registered domain per 7 days; **5 duplicate
  certificates per identical identifier set per 7 days**; 300 new orders per account
  per 3 hours; 5 failed authorizations per identifier per account per hour. Renewals
  are exempt from the per-domain and per-account limits but **not** from the duplicate
  certificate or failed-authorization limits.

`highgeek.eu` is hosted on **Cloudflare, DNS-only** — records resolve straight to OVH
addresses with no proxying — so the edge genuinely terminates TLS and Cloudflare's API
is available for DNS-01. Certes 3.0.4 supports DNS-01 (`ChallengeTypes`,
`IAuthorizationContextExtensions`, `IKey.DnsTxt`).

## Decision

**DCMS manages certificates for the domains DCMS owns, as an editable list, issued
over DNS-01 against Cloudflare. Tenant-owned domains keep the HTTP-01 path and the
custom-upload path, unchanged.**

Concretely:

1. A new `edge.managed_certificates` table holds the *intent* — a name and a set of
   identifiers, wildcards allowed. It is seeded with one row covering
   `highgeek.eu`, `*.highgeek.eu`, `*.dcms.highgeek.eu`: three SANs on one
   certificate, which covers every hostname vps1 serves and replaces twelve rows
   with one.
2. Superadmins add, edit and disable managed certificates from the platform console,
   and can see issuance state and attempt history.
3. Wildcard orders validate over DNS-01 by writing TXT records through the Cloudflare
   API with a zone-scoped token held in Vault.
4. **HTTP-01 stays** as the path for a domain a tenant points at us, and custom
   certificate upload stays exactly as built. Uploaded certificates are served and
   never renewed; their expiry is reported, not fixed.

### Why not ZeroSSL

Adding ZeroSSL as an alternative CA was designed and rejected in the same session. It
would have traded Let's Encrypt's per-registered-domain limit for ZeroSSL's per-account
limits — a real escape hatch, but it treats the symptom. One wildcard certificate
removes the ceiling structurally and reduces the platform to a single order every
sixty days.

### Why not per-domain DNS credentials

Superadmins can add any domain whose zone lives in the platform's own Cloudflare
account; the zone is discovered by walking labels upward at issuance time. Storing a
third party's DNS-edit token to renew their certificate is a large standing risk for a
case that does not exist yet. CNAME delegation (`_acme-challenge.their.com` → a record
we control) is the better answer if it ever does, and this data model leaves room for it.

The console validates **syntax only** when a certificate is saved, not whether we hold
the zone. Answering that needs the Cloudflare token in admin-api, and the edge's Vault
policy is narrow on purpose — one Transit key and its own secrets, so a compromise there
stops at TLS. Copying a DNS-edit credential across to improve a validation message would
undo that. Cloudflare's own sentence ("no zone in this account contains…") is lifted
verbatim into the attempt row instead, so the answer arrives within a sweep and is
visible in the console.

## Consequences

- **The blast radius of a failed issuance changes shape, and this is the main risk.**
  Today a broken hostname costs that one site. With a single certificate covering the
  platform, five failed issuances of the identical identifier set in a week lock out
  *every* hostname for a week. The existing `ConsecutiveFailures` backoff does not
  cover it — `EdgeTlsPreflight` deliberately *clears* that on every restart, which is
  right for per-host HTTP-01 and would be catastrophic here. So managed certificates
  carry a separate ceiling, counted in `edge.managed_certificate_attempts`, that a
  restart cannot reset: at most 3 attempts per identifier set per 7 days, under Let's
  Encrypt's 5, with headroom.
- **A certificate now covers more than one hostname**, so the handshake lookup gains a
  wildcard fallback: exact hostname first — which preserves today's precedence and lets
  a tenant's uploaded certificate override the platform wildcard — then the wildcard
  parent. The certificate cache is keyed by *requested* hostname, so one wildcard is
  cached under many keys and replacing it must invalidate all of them; a store-wide
  generation counter in the cache key does that in O(1).
- **The edge gains an outbound dependency on the Cloudflare API** and a new package,
  `DnsClient.NET`, used to confirm TXT propagation against the zone's authoritative
  nameservers before asking the CA to validate. Authoritative rather than a recursive
  resolver, because that is what Let's Encrypt queries — a cached negative from a
  recursor would report the opposite of what the CA will see.
- **A missing or rejected Cloudflare token is not backed off.** It raises
  `CertificateIssuanceUnavailableException`, which the callers already treat as "the CA
  was never asked, so nothing was spent" — the same distinction introduced when an
  expired Vault token was being charged to the CA's budget.
- **Rollout has no flag day.** The wildcard ships alongside the existing per-host
  certificates; because matching is exact-first, those keep serving until they expire
  and the wildcard is only reached for names with no exact row. Rollback is disabling
  one row.
- **`highgeek.eu` has no CAA records**, so any CA may issue for it today. Adding
  `0 issue "letsencrypt.org"` and `0 issuewild "letsencrypt.org"` becomes worthwhile
  once the platform holds a Cloudflare token that can write them.
- `edge.managed_certificates` and `edge.managed_certificate_attempts` carry **no
  `TenantId`**, for the reason already recorded on `EdgeCertificate`: a certificate
  belongs to a hostname, and tenant ownership derives from `tenancy.domains`. That
  keeps them legitimately outside `RlsConfigurator.TenantTables` rather than missing
  from it — see [ADR 0005](0005-rls-defense-in-depth.md). If either ever gains a
  tenant column it must be registered there and in `AssertRlsCoverage` in the same
  commit.
- **The issued chain is now assembled from what the CA returned**, not rebuilt by Certes from
  a root list compiled into the library. `chain.ToPem()` walks each certificate's issuer
  through that list and throws `"Can not find issuer"` on one it does not know — so issuance
  would break the day a CA rotates to an unfamiliar intermediate or root, *after* the
  certificate had been issued and counted against the rate limit. Certes was last released in
  2021. The replacement concatenates the leaf and the issuers from the ACME response, which is
  also the correct set to serve: leaf plus intermediates, no root. This is a change to the live
  TLS path and is covered end to end against Pebble (`WildcardIssuanceTests`), which the
  previous behaviour never was.
- **Certes has no ARI.** Let's Encrypt exempts ARI-driven renewals from every rate
  limit, which would make the ceiling above unnecessary. Revisit if Certes adds it, or
  if the ACME client is swapped behind `IAcmeIssuer`.
