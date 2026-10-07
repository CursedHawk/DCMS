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
| Field | Types: text, longText, integer, decimal, boolean, date, dateTime, email, url, choice, multiChoice, media, json. Flags: required, unique, searchable, sortable, filterable, readOnly (only flows may set it), hiddenFromPublic. |
| Relationship | Many-to-one (a lookup field on the source table), one-to-one, or many-to-many. Has an `onDelete` rule. |
| Choice set | Named options that choice fields share. |
| View | A saved filter, sort and column list over a table; one view per table is the default. |

**Renaming a field's api name once it is published is refused.** Add a new field and mark the
old one deprecated instead: clients and flows depend on the name.

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

## Automations (flows)

A flow is a **trigger**, an optional **condition** and up to 50 **steps**.

### Triggers

| Trigger | Fires when |
| --- | --- |
| `row.created`, `row.updated`, `row.deleted` | A record in the trigger's table changes. `row.updated` can be narrowed to a list of fields. |
| `relation.created`, `relation.deleted` | A many-to-many link is added or removed. |
| `revision.published` | The app is published. |
| `flow.event.<name>` | Another flow announces it with `event.publish@1`. |
| `visitor.registered` | A visitor signs up on the site. This needs the Visitor accounts plugin. |
| `form.submitted` | A form of the Forms plugin is sent. `event.payload.data` holds what was sent. |
| `user.invited`, `user.activated` | A site user is invited, or accepts (or first signs in through a trusted provider). `event.payload.email` holds their email. This needs the User Authentication plugin. |
| `schedule` | Every N minutes, from 5 minutes up to one week. |
| `manual` | A person starts the flow, or another flow does with `flow.invoke@1`. |

### Expressions and templates

Conditions and `{{ }}` holes in step inputs use a small expression language.

- **Names** an expression can read:
  - `event`: the whole envelope, with `event.payload`;
  - `row` and `previous`: the changed record, and the changed fields' old values;
  - `changedFields`;
  - `input`: the input of a manual flow;
  - `steps.<id>`: an earlier step's output;
  - `run`.
- **Operators:** `== != < <= > >= && || ! in + - * / % ?:`.
- **Functions:** `len lower upper trim contains startsWith endsWith coalesce string number
  round now today addDays addHours join`.

Example: `row.amount >= 10000 && row.stage != 'lost'`.

Evaluation is bounded in steps and string size, and it has no way out of the run's data. Inside
an email's `html`, every template value is HTML-encoded and the whole body is sanitized.

### Actions

The Automation tab lists every action available to flows. The built-in actions are:

- `records.create|update|delete|lookup|query@1`
- `flow.invoke@1` and `event.publish@1`
- `dcms.email.send@1` and `dcms.notifications.raise@1`
- `content.get|list@1`
- `visitor.lookup@1`

Other plugins can add actions through `automation.actions@1`; see
[plugins.md](plugins.md#offering-actions-to-dynamic-apps-flows). With Visitor accounts
installed, for example, flows also get `visitor-auth.set-attributes@1`; with User Authentication,
`user-auth.invite@1`, `user-auth.add-to-group@1` and `user-auth.remove-from-group@1` (people by
email, groups by id or name; each needs *Manage site users*).

A provider's action can require that plugin's own permission;
`visitor-auth.set-attributes@1`, for example, needs *Edit visitor profiles*. Publishing an app
vouches for every flow in it, including flows that can set such a flow off (`flow.invoke`,
`event.publish`, or writing a table its row trigger watches). So:

- Publishing or rolling back an app whose flows use such an action needs the permission, even
  for an unrelated edit. Validation reports `action-not-permitted` naming the flows.
- Starting such a flow by hand needs it too.
- The permissions checked are stored with the revision. At run time an action runs only if its
  permission was checked when the revision went live; otherwise the step fails and the app must
  be published again by someone who holds it.

Once published, a flow acts with the publisher's say-so for everyone who can trigger it: for
example, members who write records, or visitors where public access allows. Choose its trigger
with that in mind. A provider can only offer actions named after itself.

An action is named with its major version. Its meaning never changes within that version.

### Runs, limits and retries

- **Each run is pinned** to the revision and flow definition that started it. The run history
  shows every step with its input, output, error and timing, plus the chain of runs that one
  original action set off.
- **Failure handling** depends on the cause:
  - A step that fails on bad input or a missing record fails the run at once.
  - Passing trouble, such as the database or a rate limit, is retried up to three attempts.
  - A failed run can be retried by hand. That needs the `flows-run` permission.
- **Limits** stop runaway cascades: 5 flows deep, 20 runs per original action, 100 record
  writes and 60 seconds per run. A run stopped by a limit ends as *terminated* and records the
  reason.

## Records and the public API

- **Members** work with records on the Records screen. The admin API for records lives at
  `/api/admin/plugins/{slug}/_records/{table}`. Bulk update and bulk delete take up to 500
  records.
- **Public access is per table**, on the Access tab. A table can be readable by everyone or
  only for each visitor's own rows. Visitors can also be allowed to create rows, and to update
  or delete their own. Fields marked hidden-from-public never leave the server.
- **The site API** is `/api/{slug}/data/{table}` on content-api. It is generated from the live
  revision only, and it is in the site's OpenAPI document and generated client.
- **Public writes are throttled** to 120 an hour per visitor and per IP.
- **Enterprise users get more through roles** (User Authentication, [user-auth.md](user-auth.md)).
  A role holding `dynamic-apps:{slug}:table:{table}:read|create|update|delete` lets its holders do
  that through the site API whatever the table's public access, on every record. A role holding
  `dynamic-apps:{slug}:flow:{flow}:run` lets them start that manual flow with
  `POST /api/{slug}/flows/{flow}/run` (`{ "input": { … } }`, at most 16 KB). The role editor lists
  every published table and manual flow.

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

A good request names the outcome, for example: *"Add a B2B CRM: companies, contacts and deals
with a stage choice; email the deal owner when a deal over 10 000 is won."*

## Operations

- **Storage:** everything lives in the shared Postgres schema `apps`, under row-level
  security.
  - The outbox and the flow-schedule queue are drained across tenants.
  - Routed events are kept for 7 days, and finished runs for 30.
- **Where it runs:** the automation worker runs inside admin-api, with no extra service. The
  database is its queue (`FOR UPDATE SKIP LOCKED` with a lease), so replicas share the work.
- **Traces:** spans are `dcms.dynamicapp.query|mutation|flow|flow.step`. They are tagged with
  tenant, instance, table, revision, flow id and version, run, step, action and status.
