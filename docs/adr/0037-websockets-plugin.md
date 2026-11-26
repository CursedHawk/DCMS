# ADR 0037: WebSockets plugin — typed realtime channels for sites, Functions and plugins

**Status:** accepted (2026-11-26) · kanban epic #660 · builds on ADR 0004 (live chat), 0016/0017
(contracts), 0029/0030 (Functions)

## Context

Tenants want realtime on their sites: notifications, chat, live counters. The only site-facing
realtime is LiveChat's `/hub/chat` (ADR 0004). It serves one feature, and it has three flaws that
must not be copied:
- it takes the tenant from `?tenant=` instead of the tenant `SiteHostResolution` already resolved
  from the Host;
- it never checks the plugin is enabled (hub endpoints skip endpoint filters);
- it calls `AddStackExchangeRedis`, which is per process, not per hub: a second caller silently
  replaces its channel prefix.

**Asked for (user, 2026-11-26):** a standalone plugin "WebSockets", SignalR with a Redis backplane,
served from content-api, usable from sites, from Functions with full typing and from other
plugins; a typed site client and OpenAPI entry when the plugin is enabled; better typed client
generation for React sites; never mixed with admin-api's SignalR.

## Decision

1. **One hub per plane, its own backplane.** `WebSocketsHub` at `/hub/ws?instance={slug}` is mapped
   by the plugin's `MapHostEndpoints` on the site plane only. Its lifetime manager is a **closed**
   `HubLifetimeManager<WebSocketsHub>` (`RedisHubLifetimeManager`, channel prefix `dcms-ws`),
   registered by the plugin on every plane. DI prefers the closed type over the open generic, so
   the hub never touches `dcms-chat`, `dcms-notify` or `dcms-console`, and nothing else touches it.
   admin-api and jobs-worker never map the hub; they send through `IHubContext<WebSocketsHub>`.

2. **The connection's tenant is the Host's.** Tenant and environment come from the request's
   resolved tenant (`X-Dcms-Tenant`/`X-Dcms-Environment` set by `SiteHostResolution`), never the
   query string. A connection without `X-Dcms-Site` is refused: the edge's admin-host `/hub/**`
   route also reaches content-api, and the hub is for tenant sites only. `OnConnectedAsync` checks
   the instance is enabled, because endpoint filters do not run for hubs.

3. **Channels are declared in the instance config.** Each channel has a name, `keyed` (subscribe to
   `name/key`), who may subscribe (`public`, `signedIn`, `{roles}`, `self` = key is the caller's
   id), who may publish from a browser (`none`, `subscribers`, `{roles}`), whether a browser publish
   raises `websockets.message.received`, and an optional payload **JSON Schema** in a restricted
   subset (`type`, `properties`, `required`, `items`, `enum`, `const`, `description`, `format`,
   length and range bounds; depth ≤ 6). The subset is what the visual builder, the TypeScript and
   the C# generators can all express. Config is validated on save; every payload is validated on
   send. Config lives in the instance's jsonb, so environment deployments promote it: no tables,
   no migration, no RLS change.

4. **Contracts.** `websockets.channels@1` (`Send`, `SendToUser`; exposed on the admin plane, so
   functions `uses.contracts` it and plugins `Consumes` it) and a separate write contract
   `websockets.channel-admin@1` (AI plane only) for the assistant, so a function that sends never
   vouches for editing channels. A browser publish on a channel with `raiseEvent` is published as
   the plugin event `websockets.message.received`.

5. **One typing pipeline.** The channel schemas feed:
   - the tenant OpenAPI document (`x-dcms-realtime` plus component schemas) and the generated
     site client (`api.<slug>.realtime`, typed `subscribe`/`publish`, a `useChannel` hook);
   - the Functions SDK: `Send`'s input refined per instance into a union discriminated by
     channel (TS) or one overload per channel (C#), and the `on.websocket(instance, channel)`
     trigger with a typed payload.
   The generators gain one shared JSON-Schema→TypeScript converter (`$ref`, unions, intersections,
   `const`) and typed C# contract operations, for every plugin.

6. **The site runtime speaks SignalR's JSON protocol over the native `WebSocket`.** No dependency
   is added to vendored site runtimes. Sites refresh their generated client through the IDE, as
   today (user decision: no auto-commit to site repos).

## Consequences

- Realtime traffic is not audited; channel configuration changes are.
- Caps: 16 KB per browser frame, 32 KB per server send, 50 subscriptions and 20 publishes per 10 s
  per connection, a per-tenant connection cap per replica (to move to Redis when replicas ship).
- A function triggered by a browser message runs through the run queue (about a second or more):
  fit for moderation and persistence, not request/response — that is what HTTP functions are for.
- LiveChat keeps `/hub/chat` for now; moving it onto WebSockets is a follow-up (kanban #669).
