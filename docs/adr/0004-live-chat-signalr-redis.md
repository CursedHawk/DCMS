# ADR 0004: Live chat via SignalR with a Redis backplane in content-api

**Status:** accepted (2026-06-15) · **Phase:** 12

## Context

The LiveChat plugin needs realtime, bidirectional messaging between website
visitors and tenant agents, working across multiple horizontally-scaled
content-api replicas. Visitors are anonymous or hold a per-tenant visitor token;
agents are platform users who manage the tenant from the admin SPA.

## Decision

- The SignalR `ChatHub` is hosted in **content-api** (the read-mostly, scalable
  delivery plane) at `/hub/chat`. Cross-replica fan-out uses the
  **StackExchange.Redis backplane** (reusing the existing Redis), so a message
  posted on one replica reaches subscribers connected to any replica.
- Delivery is partitioned by SignalR groups: `conv:{conversationId}` for the
  participants of a conversation and `agents:{tenantId}` for a tenant's agents.
- **Tenant** is resolved from the `?tenant={slug}` query string at connect time
  (the WebSocket transport can't carry custom headers), not from the Finbuckle
  middleware. Hub DB access therefore uses explicit `TenantId` predicates with
  `IgnoreQueryFilters()` rather than the ambient query filter.
- **Agent authorization:** a connection is treated as an agent only if it
  presents a valid platform JWT (carried in the `access_token` query string, the
  SignalR convention) **and** the authenticated user is a member of the resolved
  tenant. Membership is checked against the `tenancy` schema, which content-api
  already reads — this avoids depending on a `tenant_ids` token claim, which per
  [ADR 0003](0003-tenancy-owned-by-admin-api.md) we deliberately do not issue.
  content-api validates the platform token with the `dcms-admin-api` audience.
- A `chat.message.posted` event is published to the NATS **CHAT** stream on every
  message. Realtime delivery is already handled by the backplane; this event is
  the out-of-band fan-out point (admin-api's `ChatFanoutConsumer`) for
  offline-agent notifications and unread tracking — a documented MVP extension.
- The agent console (admin SPA) reads conversation lists/history from **admin-api**
  REST (`/api/admin/chat/...`, gated by `chat:read`) and connects to the
  content-api hub for realtime. The visitor widget is a framework-agnostic
  embeddable (`@dcms/site-components` `createChatWidget`) usable from prerendered
  Mode A sites without a hydration runtime; it reaches the hub through site-host's
  `/hub` proxy.

## Consequences

- Chat scales with content-api; no new container for the MVP. The plugin boundary
  (hub + Redis backplane + NATS event) keeps later extraction a registration move.
- Because tenant comes from the query string, every hub DB call is explicitly
  tenant-scoped — consistent with the other cross-tenant consumers.
- Accepting the `dcms-admin-api` audience at content-api couples the two resource
  servers' audience; a dedicated `dcms.chat` scope is the clean future split.
