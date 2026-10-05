# Content from other services

A **connection** brings in data from another service's API — tickets, bookings, a product catalogue.

1. In the admin, open **Connections** and add one: its base URL, its key, and the operations (paths) to fetch.
2. DCMS fetches them on a schedule and stores the answers. The key is encrypted and never reaches a browser.
3. In the builder, a connection's operations appear as **Content** for a Collection or a detail page, just like a plugin's.

**Refresh now** on the connection fetches the latest data straight away.
