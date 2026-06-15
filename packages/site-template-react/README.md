# @dcms/site-template-react

Constrained Vite + React template for **Mode B** (generated React app) sites.

The AI emits a whitelisted file map (path → content) into this layout;
site-builder materializes it, runs an offline `pnpm install` (pre-warmed
store, pinned lockfile — the AI cannot add dependencies) and `vite build`
inside a sandboxed container (no network, CPU/memory/time limits).

Filled in Phase 10. The template pre-wires `@dcms/api-client` against the
tenant's content API.
