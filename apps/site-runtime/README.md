# @dcms/site-runtime

Mode A (published static sites) runtime, implemented in Phase 8:

- `prerender.ts` — executed by site-builder (Node) over a site-definition
  snapshot; renders each page to static HTML via react-dom/server.
- `hydrate.ts` — client entry bundled once per build; hydrates interactive
  components and executes data bindings against the tenant content API.
