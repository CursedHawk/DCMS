# Architecture Decision Records

One file per decision, numbered. The major architectural decisions made during
planning (Postgres FTS for search, SignalR + Redis for chat, DB-polling for
scheduled publishing, shared-table tenancy + RLS, bucket-per-concern MinIO
layout, media proxy-streaming, Mode B build sandboxing, no .NET Aspire, JWTs
without permission claims) are recorded in the project plan and get an ADR here
as their implementation phase lands.

- [0001](0001-imagesharp-pinned-to-3x.md) — ImageSharp pinned to 3.x
- [0002](0002-awesomeassertions-over-fluentassertions.md) — AwesomeAssertions over FluentAssertions
- [0003](0003-tenancy-owned-by-admin-api.md) — Tenancy & authorization data owned by admin-api
- [0004](0004-live-chat-signalr-redis.md) — Live chat via SignalR + Redis backplane in content-api
- [0005](0005-rls-defense-in-depth.md) — Postgres RLS as a defense-in-depth backstop
- [0006](0006-mode-a-html-css-in-git.md) — Mode A sites are HTML/CSS files in git, edited with GrapesJS
- [0007](0007-audit-logging.md) — Platform-wide audit logging: transactional outbox + HMAC hash chain
- [0008](0008-observability.md) — Observability: OpenTelemetry into a single-host LGTM stack
- [0009](0009-in-app-notifications.md) — In-app notifications: admin-api hub, fan-out on write
- [0010](0010-yarp-edge.md) — The public edge is a .NET service (`Dcms.Edge`), not Caddy
- [0011](0011-wildcard-tls-dns01.md) — DCMS-managed wildcard certificates over DNS-01
- [0012](0012-platform-notifications.md) — Platform notifications: addressed to a role, no fan-out
- [0013](0013-platform-console-live-updates.md) — The platform console gets a socket of its own (supersedes 0012's polling)
- [0014](0014-admin-console-bff.md) — The admin console authenticates by session cookie at the edge, not by a token in `localStorage`
- [0015](0015-forced-rls.md) — The database enforces tenant isolation, not just the ORM (supersedes 0005's deferral; implemented)
- [0016](0016-plugin-contracts.md) — Plugins own their code and meet through versioned contracts
- [0017](0017-plugin-api-ecosystem.md) — Every plugin is an API: `.Api` packages, open contracts, hooks, installed plugins
- [0018](0018-plugin-admin-pages.md) — A page per plugin instance; plugins describe data sets, the console renders them
- [0019](0019-plugin-screens.md) — Plugins ship their own admin screens and menu entries; permissions are theirs to add
- [0020](0020-mode-d-react-visual-builder.md) — Mode D: a visual builder whose output is a real React application
- [0021](0021-dynamic-apps.md) — Dynamic Apps: a tenant-defined application runtime inside the plugin boundary
- [0022](0022-user-authentication.md) — User Authentication: tenant-scoped enterprise users, signed in by Identity, enforced at the edge
- [0023](0023-google-analytics-plugin.md) — Google Analytics as a plugin: a Measurement ID per site, looked up at runtime, loaded after consent
