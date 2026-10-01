# ADR 0019: Plugins ship their own admin screens; permissions are theirs to add

**Status:** accepted · implemented (2026-10-01)

## Context

[ADR 0018](0018-plugin-admin-pages.md) gave every instance a page and let plugins describe data
for the console to render, and deliberately stopped there: no plugin-shipped UI, because UI code
in the admin's origin would be untrusted script with an admin's session. That left the Forms
inbox, the chat console, the analytics dashboard and the Meta account picker as console code
that knew plugins by name, a menu that listed Forms, Analytics and Chat whether or not a tenant
had them, and no way at all for an installed plugin to have a screen of its own. A switched-off
plugin's data could not be browsed. Plugins could declare permissions, but an outside plugin
could not enforce one on its routes, describe it, or make it a default.

## Decision

### Plugins ship real React screens

The trust argument behind ADR 0018's refusal does not survive ADR 0017: an installed plugin
already runs in-process with the host's rights, so its admin script adds no trust boundary the
operator has not already crossed by installing it. Tenants still cannot install anything.

- The manifest **declares** screens (`AdminScreens`: id, title and translations, scope, the
  permission that opens it, an optional menu placement). The platform draws the menu and guards
  the route from that alone, without loading plugin code. A plugin-wide screen is a page,
  `/app/{pluginId}/{id}`; an instance screen is a tab of the instance page,
  `/plugins/{slug}/{id}`.
- The plugin's **admin UI module** implements them: `definePluginAdmin({ screens,
  configWidgets, locales })` from `@dcms/plugin-ui`, the SDK through which a screen reaches the
  console (API client, permissions, translations, auth headers, navigation, an embeddable data-set
  view) without importing console internals.
- **Built-in plugins** keep their UI beside their C# (`src/Plugins/{Assembly}/admin`, a pnpm
  package); the console compiles every such folder in, lazily, by folder name. **Installed
  plugins** ship `admin/index.js` (+ `index.css`) in their folder, built with the SDK's Vite
  preset, which points React, `@dcms/ui`, react-query, i18n and the SDK at the console's own
  copies through a global registry the console fills before loading any plugin (Rollup synthetic
  named exports, so plugin code imports normally). The files are served anonymously from
  `/api/admin/plugin-ui/assets/{pluginId}/…`, confined to the plugin's `admin/` folder.
- **Menu entries come only from enabled instances.** `/forms`, `/chat` and `/analytics` left the
  platform's list and the console's code; the Forms inbox, the chat console, the analytics
  dashboard and the Meta account widget moved into their plugins with their translations. The
  old URLs redirect (query string kept); notifications link to the new ones.
- A screen renders behind its permission, in its own error boundary, and every way it can fail to
  appear says why (not in this console, switched off, not added, failed to load).
- Config widgets: any `format` with a registered widget is routed to it; a plugin's widgets are
  added to its settings and install forms.

### Permissions are the plugin's to add, describe, default and enforce

`PermissionDefinition` gains a description (shown in the role editor) and `GrantToMembers`,
applied to the Member role once, when a tenant adds its first instance of the plugin. Wherever a
manifest or route names a permission, a bare action is the plugin's own key. The SDK states what
gates and records a route — `RequirePluginPermission`, `WithoutPermission`, `AuditAs`,
`SkipAudit` — and the runtime turns those into the platform's metadata when it mounts the routes
(`AuditAs` opens its entry before the handler runs, as `WithAudit` does), so an outside plugin is
held to the same coverage rules as a built-in one.

### Also

- Data sets of a disabled instance are served: switching a plugin off hides it from sites, not
  its data from its owners.
- Host routes get the plugin's tenant-wide `IPluginContext` when the request names a workspace.
- A provider may handle its own events and intercept its own hooks without consuming its own
  contract.
- `samples/Dcms.Plugins.Sample.Guestbook` replaces the Greeter: one plugin using every part of the
  system, admin UI included, installed from a folder by the integration tests and loaded by URL in
  the e2e suite.

## Alternatives rejected

- **Schema-only screens** (ADR 0018's data sets, stretched): cannot express a live chat console or
  a chart dashboard.
- **An iframe per plugin**: no shared components or theme, auth and permissions duplicated across a
  frame boundary, and a second scrolling context inside every page.
- **Module federation**: a heavyweight toolchain for one host; a registry of the handful of modules
  the console shares does the same job.

## Consequences

- The list of shared modules (`@dcms/plugin-ui/shared`) is public surface: adding one is a promise
  to every installed plugin, removing one breaks them.
- Plugin screens are trusted code; installing a plugin is trusting its UI as much as its server code.
- A built-in plugin without an `admin/` folder has no screens even if its manifest declares some;
  the console says so where they would be.
- `@dcms/plugin-ui` is not yet published as an npm package; outside this repository it is consumed
  from source until CI publishes it beside the NuGet packages.
