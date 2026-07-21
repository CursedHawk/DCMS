# @dcms/site-template-react

A minimal Vite + React + TypeScript starter for a DCMS tenant site. It is wired to
the tenant content API through `src/api` and the `@dcms/api-client` runtime.

## How it's used

This package is the canonical scaffold. The admin app embeds it and, on
**API Docs → Download React starter**, materializes a per-tenant copy where:

- `src/api/` is replaced by the tenant's **generated, fully-typed** client
  (`api.<instanceSlug>.<contentType>.list()`), self-contained (runtime vendored under
  `src/api/runtime/`), so the download needs only the pinned React/Vite toolchain.
- `.env` is pre-filled with the tenant's domain and a demo instance.

In this workspace copy, `src/api` re-exports the generic `@dcms/api-client` runtime so
the template builds standalone; both expose the same `createTenantClient`, so the app
code is identical either way.

## Develop

```bash
pnpm install
cp .env.example .env   # set VITE_DEMO_SLUG / VITE_DEMO_CONTENT_TYPE (see the admin API Docs page)
pnpm dev
```

`pnpm build` runs `tsc --noEmit && vite build`. Content is fetched with
`api.content(slug, contentType).list()` (generic) or, in a downloaded copy, the typed
`api.<slug>.<contentType>` tree.
