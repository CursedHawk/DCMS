# ADR 0010: The public edge is a .NET service (`Dcms.Edge`), not Caddy

**Status:** accepted (2026-09-04) — supersedes the edge parts of
[ADR 0008](0008-observability.md)

## Context

The public ingress was `caddy:2` with a bind-mounted `Caddyfile`. It worked, and it
was the one part of the stack outside the product:

- **Certificates were invisible to DCMS.** Caddy's `/data` was the only record that a
  tenant domain had a certificate, when it expired, or why issuance failed. The admin
  UI could show "verified" and nothing more. Custom-certificate upload and per-domain
  TLS policy were not expressible at all.
- **Routing was a file.** Changing it needed a container *restart* — a plain reload
  re-read a stale inode, a trap this stack had already been caught by.
- **Auth stopped at the edge.** Every gated surface re-implemented login: Grafana
  carried a whole `generic_oauth` client against identity, Forgejo had its own
  accounts, and there was nowhere to say "this route requires SuperAdmin".
- **Two ecosystems.** Caddy config, `caddy_http_*` metrics and Caddy's storage locking
  shared no types, telemetry, logging or Vault wiring with the nine .NET services
  around it.

YARP was already a dependency, and site-host already used its direct forwarder.

## Decision

Replace it wholesale. `src/Services/Dcms.Edge` owns `:80`/`:443`:

- **TLS** through Kestrel's `TlsHandshakeCallbackOptions`, selecting a certificate per
  SNI name from `edge.certificates` — private keys encrypted with Vault Transit, the
  same mechanism identity uses for its Data Protection key ring.
- **ACME** (Certes, HTTP-01) behind an `IAcmeIssuer`. Issuance is gated by site-host's
  `/internal/tls-allowed`, so the edge cannot mint a certificate for a hostname no
  tenant owns — without that gate it is an open relay for someone else's rate limit,
  and eventually for ours.
- **Routing** from `PlatformRoutes` (static, so the edge boots before Postgres is
  reachable) overlaid with `edge.routes`, hot-reloaded through `IProxyConfigProvider`.
- **Authentication at the boundary**: cookie + OIDC against identity, per-route
  `AuthorizationPolicy`, and identity asserted downstream in each service's own header
  dialect — `X-WEBAUTH-USER` for Grafana's `[auth.proxy]` and Forgejo's reverse-proxy
  auth. Both consoles stopped implementing their own login.
- **Identity gets its own host**, `auth.highgeek.eu`. The issuer was a path prefix on
  the admin console's name; it is now a name of its own, which is why the CORS list in
  identity became load-bearing rather than a dev-only convenience.

There is **no fallback**. Caddy, `infra/caddy/`, the import path, the volumes and the
rollback lever are deleted. A bad ingress is fixed by pushing to `master`.

## Consequences

**What this bought.** Certificate state is a table: issuer, validity window, source,
last error, and a "reissue now" button, all in the admin UI. An unauthorized request to
Grafana is refused *before* it reaches Grafana, so a non-SuperAdmin never appears in its
access log. One sign-in for the whole platform. Route precedence is asserted by unit
tests rather than by the order of blocks in a file. Edge metrics arrive over OTLP with
everything else, so `dcms_edge_*` sits beside every other DCMS meter.

**What it costs.** Self-managed public TLS: a bug here is the whole platform, not one
service. Three mitigations carry that weight, and each exists because its absence has a
silent failure mode:

1. **The renewal sweep issues as well as renews.** Every pass it backfills every
   allowed hostname with no row — the platform's own names included, which are *not*
   rows in `tenancy.domains` and so bypass the tenant gate. Without the backfill an
   empty store is permanent: a hostname with no certificate is only ever attempted
   inside a visitor's handshake, bounded by `OnDemandTimeoutSeconds`, and a full ACME
   order usually takes longer — so the first visit fails, records a failure, backs off,
   and the next fails further away. A browser shows `ERR_CONNECTION_CLOSED` forever.
2. **`UseUntrustedHeaderScrubbing` runs before anything reads a header.**
   `X-WEBAUTH-USER` is a bearer credential in header form: whoever can set it on a
   request reaching Grafana *is* that user. The scrubber and the fact that neither
   Grafana nor Forgejo publishes a host port are the only two things making that safe,
   and there is a test asserting the first.
3. **The health probe asserts a non-empty route table.** A proxy that starts with no
   routes answers every request with a 404 while every signal stays green.

**Two traps found in the build**, recorded because neither is guessable:

- **YARP validates the proxy config as a whole.** One route naming a policy the
  container did not register rejects the *entire* table, and the edge 404s everything.
- **Forgejo usernames are not derivable.** `ForgejoUserSync` allocates them from the
  email local part with a `-2` suffix on collision, so an edge that recomputed the name
  would, on any collision, sign one user in as another in the git server holding every
  tenant's repositories. The name travels as a `forgejo_username` claim, and Forgejo's
  auto-registration is off so a guessed name cannot collide with an allocated one.

**Accepted losses.** OCSP stapling (Let's Encrypt stopped serving OCSP in 2025;
browsers rely on CRLite and short lifetimes) and ACME Renewal Information (not in
Certes — renewal is at 30 days remaining, with jitter).

**Not folded in:** `SiteHost/ApiProxy`. The premise that "the edge already resolves
host → tenant" is false — it asks whether a hostname is *allowed*, never whose it is,
precisely so the most exposed process on the platform holds no grant on
`tenancy.domains`. Folding it in would mean widening that grant or replicating the
host → tenant cache with its own TTL, where a stale entry is one tenant's data answered
to another. `docs/runbook.md` records the version worth building instead.
