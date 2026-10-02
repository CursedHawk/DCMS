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
