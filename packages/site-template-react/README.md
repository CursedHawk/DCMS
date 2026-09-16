# @dcms/site-template-react

The starter templates a new DCMS Mode B (React) site begins from. admin-api embeds this package
(see `src/Services/Dcms.AdminApi/ApiClientGen/SiteTemplates.cs`); the editor's template picker and
the API Docs "Download React starter" both materialise from it.

## Layout

| Path | What |
| --- | --- |
| `shared/` | Files every site gets: `index.html`, Vite/TS config, `src/lib/api.ts`, `src/lib/useApi.ts`, `src/styles/tokens.css` + `base.css`, and the `src/dcms/` analytics + consent runtime. |
| `templates/blank/` | Nothing on the page yet; everything wired. |
| `templates/content/` | Multi-page content site on react-router: home, collection pages, item pages. |
| `templates/landing/` | One page: hero, features, latest items, contact form on the Forms plugin. |
| `shared/src/api/` | **A fixture, never shipped.** Real generator output for a sample tenant. |

A site is `shared/`, then the template (which wins on a clash), then the tenant's generated layer:
`src/api/` (typed client + `API.md`), `src/dcms/` and `openapi.json`. Each template carries an
`AGENTS.md` describing its layout for authors and for the IDE agent.

## Why `shared/src/api/` is committed generated code

`pnpm build` typechecks each template against it, so the templates are proven to compile against
what the generator actually writes rather than against a hand-written stand-in. It is pinned by
`TemplateFixtureTests` in `Dcms.IntegrationTests`, which fails when the generator's output changes.
Regenerate it with:

```bash
DCMS_UPDATE_TEMPLATE_FIXTURE=1 dotnet test tests/Dcms.IntegrationTests --filter "FullyQualifiedName~TemplateFixture"
```

Dependency versions in the templates are pinned to the IDE preview's palette
(`packages/site-builder-toolchain`), so a site behaves the same in the preview and when published.
