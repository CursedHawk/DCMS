# Dynamic Apps

Dynamic Apps lets a tenant build its own application inside DCMS: tables, fields,
relationships, views, automations, and a public API generated from them. A CRM, a booking
desk or an applicant tracker are typical. Each **instance** of the `dynamic-apps` plugin is one
application. Its whole configuration is versioned data, and nothing reaches the running app
until someone publishes it. The decisions behind this are in
[ADR 0021](adr/0021-dynamic-apps.md).

## Configure an app

Add the plugin in **Settings → Plugins** and give the instance a slug, e.g. `crm`. The app
then has two screens:

- **Configuration**, at `/plugins/crm/configuration`, holds the data model, automations,
  public access and revisions.
- **Records**, at `/plugins/crm/records`, holds the data itself.

### Draft, review, publish

- Every edit lands in the app's **one shared draft**. Each edit is a change set that names
  the draft hash it was made against.
- If someone else changed the draft in the meantime, the edit is refused with *"changed
  elsewhere"* and the screen reloads. Nothing is merged silently.
- **Review & publish** shows the diff against what is live and lists any validation problems.
  It also calls out destructive changes: a deleted field, or a new required or unique field
  over existing rows.
- Publishing makes the draft the live revision, in one transaction. A revision is frozen once
  it leaves draft.
- **Rollback**, on the Revisions tab, copies an earlier published revision into a new one and
  publishes that. History is never rewritten.

### The data model

| Resource | What to know |
| --- | --- |
| Table | `apiName` (lowercase snake_case) is the identity in the API; the display name is just a label. |
| Field | Types: text, longText, integer, decimal, boolean, date, dateTime, email, url, choice, multiChoice, media, json. Flags: required, unique, searchable, sortable, filterable, readOnly (only automations, which are functions, may set it), hiddenFromPublic. |
| Relationship | Many-to-one (a lookup field on the source table), one-to-one, or many-to-many. Has an `onDelete` rule. |
| Choice set | Named options that choice fields share. |
| View | A saved filter, sort and column list over a table; one view per table is the default. |

**Sortable and filterable fields get database indexes.** After a publish, the automation
worker gives each sortable field two indexes (ascending and descending) and each filterable one
an index, so a page sorted or filtered by it reads its rows from the index instead of sorting the
whole table. Each searchable text field (or the primary field, when none is searchable) gets a
trigram index, so free-text search does not read every record. Text-like values are indexed by their first 200 characters. An app gets at most 64
indexes; validation warns (`index-limit`) when its flags want more. Booleans, multi-choice, json and
lookups are not indexed. Flag only what lists really sort and filter by: every index costs writes.

**Renaming a field's api name once it is published is refused.** Add a new field and mark the
old one deprecated instead: clients and functions depend on the name.

### Changing a model without losing data

A record keeps each value under the field's internal id. A field that is deleted and created
again under the same name is therefore a new, empty field, and editing records beforehand does
not help: until the change is published, they still write the old field. Instead:

- **Convert a field in place.**
  - A text field can become a choice field, and a choice field a multi-choice field, by
    changing its type.
  - Publishing keeps every value, provided each one is an option. Validate and the review list
    the values that are not, with examples; blank text counts as no value.
  - Choice to multi-choice turns each value into a one-item list.
- **Carry values into a replacement.**
  - When a new field or relationship replaces a live field, choose that field under *Copy
    values from* (`copyFrom`), and delete the old field in the same draft.
  - Publishing copies the values that fit: record ids that exist, for a lookup (a `company_id`
    text field becoming the `company` relationship), and options, for a choice.
  - What does not fit is listed and left out. Each copied record's version goes up by one.

The validator flags a text `…_id` field that names another table (`reference-as-text`) and
suggests the relationship with `copyFrom`.

On the Records screen, choice fields can be changed straight from the list, and the list can
be filtered by picking some of a choice field's options.

## Automations

Automation is code now: a **Functions** app's TypeScript functions, triggered by an app's
row and relation events, its publishes, messages, schedules, platform events or HTTP, and
reaching its tables through a typed SDK generated from this model ([ADR 0029](adr/0029-functions.md)).
An app's configuration screen has a **Functions** tab: the functions its tables trigger or
use, a link to their code, and "Automate with AI".

**Flows were retired into functions** (2026-11-04): each environment's flows became the
functions of its `automations` Functions app, with the same triggers, conditions and steps.
Flows can no longer be created or changed: a change set creating or updating one is refused,
while deleting an old one is allowed. Old revisions still carry flows as data; nothing runs them.

## Records and the public API

- **Members** work with records on the Records screen. The admin API for records lives at
  `/api/admin/plugins/{slug}/_records/{table}`. Bulk update and bulk delete take up to 500
  records.
- **Bulk import** (Records → *Import CSV*, permission *Import application records in bulk*,
  `plugin:dynamic-apps:data-import`): up to 1,000 rows into one table, all or nothing. The first
  row names the fields by api or display name; `,` or `;` separates. Numbers use a dot, booleans
  are true/false/yes/no, dates yyyy-MM-dd, multi-choice values are split by `;` or `|`, and a
  lookup takes the target's id or its primary field value (a company's name). *Check* runs
  every row without writing; a failed import writes nothing and lists the problems by row.
  `POST …/_records/{table}/import?dryRun=true|false` with `{ csv }` or `{ rows }`. Every record
  fires its table's row-created automations.
- **The assistant imports too**: attach a CSV to the conversation and ask; it validates with
  `validate_import`, then imports with `import_records` after you confirm. The console sends the
  attached file's text itself (`csvFile` → `csv`), so the file never passes through the model.
- **Public access is per table**, on the Access tab. A table can be readable by everyone or
  only for each visitor's own rows. Visitors can also be allowed to create rows, and to update
  or delete their own. Fields marked hidden-from-public never leave the server.
- **Row access rules** decide, per record, which signed-in users reach it (Access tab, "Row access
  rules"). A rule follows relationships from the record to a field and matches it against the
  user's id, email, groups or a profile attribute; it grants read, change and/or delete. Typical
  setups:
  - *A permission table.* `company_access` (lookup `company` → companies, inverse `access`;
    email field `user_email`). Companies get the rule path `access`, field `user_email`, matches
    `user.email`. Adding or removing a row grants or revokes access at once.
  - *Inherited access.* Activities (lookup `company`) get path `company` → `access`, same field:
    an activity is visible exactly when its company is. From the site, a new or changed activity
    can only point at a company the user can see.
  - *A group per record.* A text field holding a User Authentication group id, matches
    `user.groups`.

  Email and groups only count for User Authentication sign-ins (a visitor account's email is
  unverified), and an attribute only when visitors cannot edit it themselves. A site write must
  leave the record within the writer's reach, or it is refused.
  Rules add to the table's read/own settings, so leave read on "no access" for rule-only tables,
  and keep the tables a rule reads admin-only (the validator warns otherwise).
- **The site API** is `/api/{slug}/data/{table}` on content-api. It is generated from the live
  revision only, and it is in the site's OpenAPI document and generated client.
- **Saved views on the site**: `/api/{slug}/views/{table}` — the app's views for anyone who may
  read the table, and a signed-in User Authentication user's own and shared ones. Visual sites
  show them in the **Data table** component; see [table-views.md](table-views.md#on-the-tenants-sites).
- **Public writes are throttled** to 120 an hour per visitor and per IP. A signed-in user holding
  a role on the app (below) counts against their own 120 instead, and role holders together
  against 1,200 an hour per IP, so an office behind one address is not limited to 120.
- **Enterprise users get more through roles** (User Authentication, [user-auth.md](user-auth.md)).
  A role holding `dynamic-apps:{slug}:table:{table}:read|create|update|delete` lets its holders do
  that through the site API whatever the table's public access, on every record. On a table with
  row access rules it reaches only the rows the rules let its holders read, unless the role has
  **Bypass row-level access** (Access → Roles in User Authentication). The role editor lists
  every published table. Something a site user starts is a function callable over HTTP with
  `access: { http: ["role"] }`.

## The assistant

The admin assistant configures and operates an app through the same commands a person uses,
under the same permissions. It works through two contracts: `dynamic-apps.config@1` and
`dynamic-apps.records@1`. The plugin instance's **AI tools** switch turns them on.

- **It works draft-first.** It reads the model, applies change sets, then validates and
  previews before asking to publish.
- **Publishing, rollback and deletes are dangerous operations.** They wait for approval
  unless the assistant's mode allows them.
- **Every change it makes is stamped** with its conversation and run id. A revision shows
  that it came from the assistant, and the audit log shows which tool call made each change.
- **Automation it writes as functions**, through the Functions app's `functions.code@1`
  (the app's Functions tab, "Automate with AI").
- **Ask AI where something went wrong:**
  - on the issues that block a publish.

  Either one opens the assistant with the question written out in the message box, and you
  choose whether to send it.

A good request names the outcome, for example: *"Add a B2B CRM: companies, contacts and deals
with a stage choice; a function that emails the deal owner when a deal over 10 000 is won."*

## Operations

- **Storage:** everything lives in the shared Postgres schema `apps`, under row-level
  security.
  - The outbox of row, relation and publish events is drained across tenants, to the
    functions on them; routed events are kept for 7 days.
- **Indexes:** `apps.sync_record_indexes`, a function the migration creates with the owner's
  rights, makes and drops them (admin-api's own role may create no index). Builds are not
  concurrent, so each holds writes to `apps.records` while it reads the table; the worker makes at
  most 4 per call, gives up on a lock after 500 ms and on a call after 15 s, and the platform spends at most 2 minutes an hour (and a tenth of the time) on index DDL; logs carry index names and SQLSTATEs only. A tenant gets at most 128 indexes across its apps.
- **Where it runs:** the worker runs inside admin-api's worker role, with no extra service. The
  outbox is claimed with `FOR UPDATE SKIP LOCKED`, so replicas share it.
- **Traces:** spans are `dcms.dynamicapp.query|mutation`, tagged with tenant, instance, table,
  revision and status.
