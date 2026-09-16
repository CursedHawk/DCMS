# Content site

A multi-page site that shows the tenant's published content: a home page with the latest items from every collection, a paginated page per collection (with tag filtering), and a page per item.

Built with React, TypeScript and Vite, and published by DCMS. **`AGENTS.md` describes how the
project is laid out** — for people and for the DCMS assistant alike.

```bash
npm install
npm run dev        # local development; set VITE_API_BASE_URL in .env to reach a remote tenant
npm run typecheck
npm run build
```
