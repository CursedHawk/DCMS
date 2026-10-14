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
- A view's filter is the tree `{ and | or | not | field op value }` — Dynamic Apps' record
  filter, so it reaches records unchanged. Content compiles it to SQL over the item's draft
  (`ContentFilterSql`); a data set gets it as `DataQuery.Where`.
- Under RLS at 6,400 items a filtered content page took about 110 ms and a filtered page sorted
  by a field about 250 ms (2026-10-14).
