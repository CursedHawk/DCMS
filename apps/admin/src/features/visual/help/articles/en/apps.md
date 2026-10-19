# Records from Dynamic Apps

An application built in **Dynamic Apps** — a CRM, a booking system, an inventory — keeps **records** in tables. A site shows them, and lets visitors add and change them, as far as the app's **public access** allows.

- **A list:** a **Data table** (search *data table*) with its **App** and **Table**. Visitors search, sort, filter and save views. Set **Open a record at** to a page like `/deals/:id` and each row links to its record.
- **Cards:** a **Collection** whose **Content** is one of the app's tables.
- **One record:** add a page with an address like `/deals/:id` and choose the table as its **Detail page** content; its parts show the record's fields. On it, a Collection over the table's related records (an account's deals) lists those of this record.
- **Adding and editing:** a **Record form** with the app and table. **Does: Edit the page's record** on a detail page changes the record that page shows; **Then open** can go to `/deals/:id` once it saved.

Who may read, add or change a table's records is set on the app's **Configuration** screen, not on a page. A table the site cannot read does not appear in the lists: publish it, or open its public access.

The **Agent** knows the apps: ask it for a CRM and it can propose the tables, for you to approve and then publish on the app's Configuration screen.
