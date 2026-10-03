# Visual React site (Mode D)

This site is built in the DCMS visual builder. Its pages are not React source: they are JSON
documents that the DCMS runtime renders. Change them through the builder, its AI agent, or by
editing the JSON exactly as described below.

## Where things are

| Path | What |
| --- | --- |
| `dcms/app.json` | Routes (`/`, `/events/:slug`), named menus, the app shell, SEO defaults. |
| `dcms/pages/<id>.json` | One page: `{ schemaVersion, id, title, seo, root }`, where `root` is a tree of nodes. |
| `dcms/theme.json` | Design tokens (colours, fonts, spacing, type scale). |
| `dcms/components/<name>/v<N>.json` | The site's own components, one immutable file per version. |
| `dcms/code/<name>.json` | A developer component's contract: `{ schemaVersion, name, label, props }`. |
| `src/components/<name>.tsx` | Its source. The default export receives the contract's props as React props. |

A node is `{ id, type, props?, slots?: { <name>: Node[] }, action? }`. Every `id` is unique in its
document. `type` is a registered component (`dcms.section`, `dcms.heading`, …); which children a
slot accepts is fixed by the component — the builder refuses anything else, and so does publish.

## Never edit these — DCMS rewrites them

| Path | What |
| --- | --- |
| `src/dcms/runtime/` | The component runtime. Updated by DCMS; local edits are lost. |
| `src/api/`, `src/dcms/` | The tenant's API client, analytics and consent. |
| `openapi.json` | The raw API description. |

`src/main.tsx` and `vite.config.ts` wire the runtime in; keep that wiring.

## Developer components

A developer component is placed and configured in the builder like any other (`type:
"code.<name>"`), but it is code: the builder's canvas shows a placeholder for it and never runs
it. It runs on the published site and in the builder's sandboxed full preview. Keep the
contract's `props` and the component's props in step — the builder edits what the contract
declares, and nothing else reaches the component.
