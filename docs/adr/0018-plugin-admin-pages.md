# ADR 0018: Every plugin instance has an admin page; plugins describe their data, the console renders it

**Status:** accepted · implemented (2026-09-30)

## Context

After [ADR 0017](0017-plugin-api-ecosystem.md) every plugin described its API, but the admin
console could only list instances and open a config dialog. The data plugins keep — visitor
accounts, form submissions, chat history, whatever an installed plugin puts in `dcms.storage` —
was reachable through a handful of hand-built screens (the Forms inbox, the chat console) or not
at all: nobody could see or fix a visitor's account. A screen per plugin does not scale, and an
installed plugin cannot ship one: its code runs in the host, but UI code in the admin origin
would be untrusted script with an admin's session.

## Decision

### A page per instance, built from what the platform already knows

`/plugins/{slug}` shows one instance: its **wiring** (contracts it offers; contracts it uses and
which instance answers each, following bindings; instances that rely on it; events it handles),
an overview (data sets with counts, content types linking into Content, scheduled jobs), its
settings (moved from the dialog), its site routes (from the tenant's OpenAPI document) with the
developer reference and console of ADR 0017, and its audit history. None of it is written for a
particular plugin. The page is gated on `plugins:manage`.

### Data sets: a declarative table the plugin answers and the console draws

A manifest lists `DataSets` (`DataSetDeclaration.Of<T>(id, title, read, write)`); `T` implements
`IPluginDataSet`: `DescribeAsync` (columns with a render kind, a JSON Schema of editable values,
filters with options, actions with a risk, capabilities), `ListAsync(DataQuery)`, `GetAsync`,
and optionally create, update, delete, actions and download. The set is constructed per request
with the plugin's context for the instance, like a route handler.

admin-api serves every plugin's sets from one group of routes, `/api/admin/plugins/{slug}/_data`,
and **the runtime is the gate**: the set's own read/write permission (default `plugins:manage`);
only what the schema declares is invoked; values are cut to the item schema and validated
against it; filters only with declared options; sorting only on sortable columns; pages capped.
Every change is audited as `plugin.data.*` against the instance. Row keys travel as `?key=`
because they may contain `/`.

The SPA renders any set with one component: search, filter selects, a server-sorted and
server-paged `DataTable`, a sheet showing a row's details and an editor generated from the item
schema, bulk and row actions (dangerous ones with a destructive confirm), downloads.

### Free sets for platform stores

A plugin consuming `dcms.storage` gets "Stored data" (its documents for this instance and
plugin-wide, editable as JSON); one consuming `dcms.blobs` gets "Files". Their ids are the
contract names, which plugin ids (kebab-case) cannot collide with. An installed plugin therefore
has a data view with no code at all, and a proper one with a single class.

First-party sets: VisitorAuth **visitors** (every attribute including private ones, sign out
everywhere, mark verified, delete), Forms **submissions**, AI Chatbot **conversations**.

## Alternatives rejected

- **Contract operations as the data API.** No notion of columns, paging, sorting or filters;
  every table would pollute the AI tool list and the site client.
- **A hand-built page per plugin.** Unusable by installed plugins, and the cost the page removes.
- **Plugin-shipped UI bundles.** Untrusted script in the admin origin; revisit with an isolated
  iframe origin if a plugin ever needs UI a schema cannot describe.

## Consequences

- A new plugin gets an admin screen by declaring data sets; the console learns nothing.
- The hand-built screens stay where they add something a table cannot (the Forms inbox across
  instances, the live chat console, the analytics dashboard); the data sets are the same data.
- The data of a disabled instance is not browsable (the instance resolver only serves enabled
  instances); enabling it again shows everything.
- `PluginConfigValidator` moved into the SDK runtime, which validates data-set values with it.
