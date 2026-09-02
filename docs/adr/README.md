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
