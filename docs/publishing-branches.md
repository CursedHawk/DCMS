# Publishing branches

A site whose source is in git (React app, visual builder, HTML builder) publishes its `release`
branch to its domains. It can publish more branches, each to its own domains: a developer's
`dev` on dev.example.com, a `staging` for review. Uploaded static sites have no branches.

## Setting one up

1. In the site's **Deployments** panel, under *Where it is live*, pick a branch and **Publish**
   (needs *Publish sites*). Its head is built at once.
2. Give it a domain: **Add a subdomain** makes a platform subdomain (`….dcms.highgeek.eu`) and
   links it, no DNS needed (needs *Manage domains*). Or on **Settings → Domains**, link any
   verified domain to the site and choose the branch in the *Branch* select.

From then on, every commit or push to that branch builds it and puts it live on its domains,
whoever made it — anyone who can edit the site can deploy a branch. Release works as before:
**Publish** merges into it.

## What differs from release

- **Kept out of search.** A branch's domains answer with `X-Robots-Tag: noindex, nofollow`.
- **Never the primary domain.** The primary is the site's own address (API docs, the builder
  preview's sign-in rules), so only a release domain can be it.
- **Same content and users.** Branches are code. Every branch reads the workspace's live content
  and plugins, and User Authentication's page rules are per domain, so add rules for a branch's
  domain if it should not be open.
- **Its own history.** Builds show their branch; going back to an earlier build (rollback) and
  **Redeploy** act on that build's branch only.

**Stop publishing** (the bin icon) unlinks the branch's domains, which stay in the workspace, and
stops its pushes building. Its builds stay in the history.

Under the hood: `sites.site_publish_branches` (one row per branch, with its live build),
`site_builds.Branch` and `tenancy.domains.Branch` (null = release). The push webhook builds
release and the listed branches, coalescing per branch.
