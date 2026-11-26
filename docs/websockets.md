# WebSockets: typed realtime channels

The WebSockets plugin (ADR 0037) gives a workspace's sites realtime channels: notifications,
chat, live counters. Each instance (an app, e.g. `realtime`) declares **channels**; browsers
subscribe to them on the site's own domain, functions and plugins send to them, and what browsers
publish can run functions.

## Channels

A channel has:

| Setting | Values | Meaning |
| --- | --- | --- |
| `name` | `a-z0-9-` | The channel. A **keyed** channel is subscribed to per key: `chat/room-42`. |
| `subscribe` | `public`, `signedIn`, `permission`, `self` | Who may subscribe. `permission` is the site permission `websockets:{app}:channel:{name}:subscribe`, granted with site roles (Users → Roles). `self` is a keyed channel whose key must be the caller's own id: a private stream per user. |
| `publish` | `none`, `subscribers`, `permission` | Who may publish **from a browser**. Functions and plugins always may. |
| `raiseEvent` | bool | Also raise `websockets.message.received` for browser publishes, so `on.websocket` functions run. |
| `schema` | JSON Schema | The payload type. Every message is checked against it; it types the site client and the Functions SDK. Absent: any JSON. |

Payload schemas use a subset every generator can type: `type` (object, array, string, number,
integer, boolean), `properties`, `required`, `additionalProperties` (true/false), `items`, `enum`,
`const`, `description`, `format` (date-time, date, email, uri, uuid), and length, value and item
bounds. The config schema enforces it, so the console, the assistant and the CLI cannot save
anything else.

### Where to edit them

- **The app's Settings**: the channel builder, with fields to click together, the JSON Schema
  (checked as you type), and the TypeScript the clients will get.
- **The web IDE**: the Channels view (radio-tower icon) opens `channels.json` for each enabled
  WebSockets app, checked against the same schema.
- **A desktop IDE**, through the dcms CLI:

  ```bash
  dcms op call websockets.channels-schema -o .dcms/channels.schema.json
  dcms op call websockets.channels --instance realtime -o channels.json
  # add "$schema": "./.dcms/channels.schema.json" to channels.json; VS Code now checks it
  dcms op call websockets.set-channels --instance realtime -b @channels.json
  ```

- **AI agents**: the assistant has `websockets.channel-admin@1` (list, put, delete) once the app's
  AI tools are on; desktop agents reach the same routes through `dcms` and its MCP server.

## On a site

The generated client (`src/api`, refreshed when the site is opened in the IDE) has
`api.<app>.realtime.connect()`, typed per channel:

```ts
const live = api.realtime.realtime.connect();
const stop = live.subscribe('chat', (msg) => console.log(msg.text), 'room-42'); // msg: ChatPayload
await live.publish('chat', { text: 'hi' }, 'room-42');  // a compile error for a server-only channel
live.onState((s) => console.log(s));                     // connecting | open | reconnecting | closed
```

A React component keeps a subscription for its lifetime with an effect:

```tsx
useEffect(() => {
  const live = api.realtime.realtime.connect();
  const stop = live.subscribe('notify', (n) => setItems((xs) => [n, ...xs]), me.id);
  return () => { stop(); live.close(); };
}, [me.id]);
```

The connection is `wss://{site}/hub/ws?instance={app}`. It reconnects with backoff and subscribes
again. Signed-in site users are recognised by the site's own session cookie; a Visitor
Authentication visitor through the client's `visitorToken`. Only pages of the site itself may
connect (the hub checks `Origin`). The OpenAPI document lists the channels under
`x-dcms-realtime`, with their payload schemas in `components.schemas`.

## In functions

```ts
import { defineFunction, on, type ChannelMessage } from '@dcms/functions';

export default defineFunction<ChannelMessage<'realtime', 'chat'>>({
  triggers: [on.websocket('realtime', 'chat')],
  uses: { contracts: ['websockets.channels@1'] },
  async handler(message, ctx) {
    if (message.payload.text.includes('http')) return;          // typed payload
    await ctx.plugins.realtime.websocketsChannels.send({ channel: 'chat', key: message.key!, payload: message.payload });
  },
});
```

C#: `[OnWebSocket("realtime", "chat")]` with `WebSocketMessage<RealtimeChatPayload>` as the input,
and `ctx.Plugins.Realtime.WebsocketsChannels.Send(new RealtimeWebsocketsChannelsSendChat { … })`,
one overload per channel.

`on.websocket` runs through the run queue, about a second after the message: right for
moderating, storing and notifying, not for answering the browser (an HTTP function does that).
The trigger needs `plugin:websockets:{app}:read` vouched; sending needs `plugin:websockets:{app}:send`.

## From another plugin

Consume `websockets.channels@1` (`ContractRequirement.Of<IWebSocketChannels>()`) and call
`SendAsync(new ChannelMessage(channel, payload, key))` or `SendToUserAsync(...)`; subscribe to
`websockets.message.received` for what browsers publish.

## Limits

16 KB per browser frame, 32 KB per server send, 50 subscriptions and 20 publishes per 10 s per
connection, `Plugins:websockets:MaxConnectionsPerTenant` (default 2000) open connections per
tenant per content-api replica, and 500 plugin calls per function run. Nothing is stored: a
message reaches the connections open when it is sent.
