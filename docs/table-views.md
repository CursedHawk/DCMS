# Table views

Every list of records in the console — a content collection, a Dynamic Apps table, a plugin's
data set — can be shaped and saved: which columns, in what order, sorted how, filtered by what.
A saved view is a member's own until they share it.

## Using them

Above the table:

- **View** — the table's default, its built-in views (a Dynamic Apps app's views, made in its
  configuration), your own views and those shared with you. A view that opens first is starred.
- **Filters** — conditions in a group that matches **all** or **any** of them; groups go inside
  groups (three levels here). Each field offers the comparisons its type takes: text contains /
  is / starts with, numbers and dates greater or less than, choices any of / none of, tags
  contain, and "is empty" for all of them.
- **Columns** — tick what to show, move it up or down.
- **Save** writes over your own view; **Save as…** makes a new one, yours alone.
- **…** — open this table with this view, **Share…**, delete.

What is on screen and differs from the view says *modified*. The view in use is in the address
(`?view=` on the content page), so a link opens the same list.

## Sharing

Only the owner changes, shares or deletes a view. **Share…** gives it, read-only, to the whole
workspace or to chosen people and roles. A shared view never opens a table: someone who may not
read that table does not see its views, and the rows they see are still only those their
permissions (and, in Dynamic Apps, the row rules) give them. Deleting a view takes it away from
everyone it was shared with and from their "opens first".

## On the tenant's sites

Dynamic Apps tables have views on the sites too (#297), for the people who use them there.

- **Visual sites** get the **Data table** component (palette → Data): set the app (`crm`), the
  table's API name (`deals`), optionally the columns shown before a view is picked, rows per
  page, and whether visitors get search, filters and columns, and saved views. Visitors sort by
  a column's header, search, filter in all/any groups (three levels) and choose columns — the
  same tree as in the console.
- **Views on a site** are the app's own (everyone who may read the table picks from them) plus,
  for a user signed in through **User Authentication**, their own and those shared with them.
  **Save** / **Save as…**, **Share**, **Delete** and **Open with this view** work as above.
  Visitor accounts (VisitorAuth) and anonymous visitors can use the app's views but not save.
- **Sharing on a site** is read-only, with **everyone signed in**, with **User Authentication
  groups**, or with **people** found by name or email. A signed-in user can therefore look up the
  site's active users (name and email) and its group names; nobody else can. A shared view still
  shows only the rows the table's public access, the user's roles and the row rules give each
  person.
- **Code sites** call the same API through their generated client — `api.{app}.{table}.views`
  (`list`, `create`, `update`, `delete`, `share`, `setDefault`) and `api.{app}.views.people` /
  `.groups` — and query with `api.{app}.{table}.query({ filter, sort, search, select, page })`
  ([dynamic-apps.md](dynamic-apps.md#records-and-the-public-api)).

Endpoints, on content-api through the site host: `GET|POST /api/{app}/views/{table}`,
`PUT|DELETE /api/{app}/views/{table}/{id}`, `PUT …/{id}/sharing` (`{ everyone, groups, users }`),
`PUT /api/{app}/views/{table}/default` (`{ viewKey }`: a view id, `system:{id}`, or null), and
`GET /api/{app}/views/_people?search=|ids=` and `/views/_groups`. A user keeps at most 50 views
per table; a view is shared with at most 50 groups and 50 people.

## What each table filters on

| Table | Fields |
| --- | --- |
| Content collection | Slug, status, updated, published, and every field of the type, including the workspace's custom fields (JSON fields excepted). |
| Dynamic Apps table | The fields the configuration marks filterable, plus created and updated. |
| Plugin data set | The columns the plugin declares filterable (see [plugins.md](plugins.md#saved-views-over-a-data-set)); columns and order work for every set. |

## Under the hood

- Views live in `tenancy.table_views` (shares in `table_view_shares`, each member's first view in
  `table_view_defaults`), keyed by table: `content:{instance}:{type}`, `apps:{instance}:{table}`,
  `dataset:{instance}:{set}`. API: `/api/admin/views` (list, save, change, delete, `sharing`,
  `default`, `audience`).
- Site views live in `apps.site_views` and `apps.site_view_defaults`, keyed by app instance and
  table id and owned by the User Authentication account. Whom to share with comes from identity's
  `/api/realm-directory/people|groups`, asked with the signed-in user's own realm token (User
  Authentication's `users.people@1`), which opens only that user's realm and only while they may
  sign in; no service credential reads it.
- A view's filter is the tree `{ and | or | not | field op value }` — Dynamic Apps' record
  filter, so it reaches records unchanged. Content compiles it to SQL over the item's draft
  (`ContentFilterSql`); a data set gets it as `DataQuery.Where`.
- Under RLS at 6,400 items a filtered content page took about 110 ms and a filtered page sorted
  by a field about 250 ms (2026-10-14).
