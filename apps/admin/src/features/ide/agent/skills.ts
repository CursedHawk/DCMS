/**
 * Platform knowledge, loaded on demand rather than pasted into every request.
 *
 * <p>The alternative is a system prompt carrying every convention DCMS has, on every turn of
 * every run, whether or not the task goes anywhere near them. On a real conversation that is
 * thousands of tokens of tax for material the model reads once in twenty runs.</p>
 *
 * <p>So the prompt advertises the <i>names</i> — a few dozen tokens — and `read_skill` fetches
 * one when it is relevant. Each entry is written for a model rather than a person: short, blunt,
 * and specific about the thing that is easy to get wrong.</p>
 */

export const SKILLS: Record<string, string> = {
  'react-site': `# Mode B site structure

- If AGENTS.md exists, it is the authority on this project's layout. Read it; this skill is the
  fallback for sites that have none.
- Entry: \`src/main.tsx\` renders into \`#root\` from \`index.html\`. Never break that wiring.
- DCMS templates share one layout: pages in \`src/pages/\` registered in \`src/routes.tsx\`,
  reusable pieces in \`src/components/\`, the API client at \`src/lib/api.ts\`, the loading hook
  \`useApi\` in \`src/lib/useApi.ts\`, site copy in \`src/site.ts\`. Extend that shape rather than
  inventing a parallel one.
- Routing: React Router 7 (\`react-router\`). The preview runs the app inside an \`about:srcdoc\`
  iframe, where browser history is not usable — the preview bundler swaps \`createBrowserRouter\`
  for an in-memory router pinned to "/". So a route you add works in production and may not be
  reachable by clicking in the preview. Verify it by reading the code, not only by clicking.
- Styling: plain CSS on design tokens — change \`src/styles/tokens.css\` before rules. Tailwind also
  works (compiled in-browser for the preview, by the real build for production).
- Static assets live beside the source and are referenced by relative path.`,

  'dcms-source-control': `# How your edits reach the site

1. You edit the WORKING DRAFT — a per-user, per-branch copy held by the server.
2. The draft autosaves. It is not a commit.
3. The user commits the draft to a branch, and publishes by merging to \`release\`.
4. Publishing triggers the real Vite build and deploys it.

Consequences:
- Your changes are visible to the user immediately and to the public not at all until they publish.
- \`git_status\` shows the draft against its branch — that is the set of changes the user will
  review and commit, so it is the honest answer to "what have you changed".
- Never assume a change is live. It is live when the user has published it.`,

  'dcms-content-api': `# Fetching tenant content

- Read \`src/api/API.md\`. It is generated from the tenant's plugins and lists every collection,
  form and call with its field names, in a few hundred tokens. \`openapi.json\` says the same thing
  in tens of thousands — do not read it for this.
- Call through \`api\` from \`src/lib/api.ts\`; never \`fetch('/api/...')\` by hand. Load in
  components with \`useApi\`, and render loading, error and empty states.
- Items are \`{ id, slug, data, publishedAt }\` — fields are under \`data\`. Reading \`item.title\`
  instead of \`item.data.title\` is the classic page that builds and renders nothing.
- \`collections\` and \`forms\` (from \`src/api\`) describe what this tenant has, including which
  field is the title, image and body. Use them for pages that should work whatever is published.
- Media fields hold asset ids: \`api.media.url(id, 'webp-960')\`.
- \`src/api/\`, \`src/dcms/\` and \`openapi.json\` are regenerated when plugins change. Never edit
  them; if a call is missing, the tenant's plugins do not offer it.
- In the preview, API calls are proxied same-origin and forced into the tenant SANDBOX, so preview
  writes never touch live content. In production they hit live content.
- \`sandbox_request\` shows an endpoint's real response when API.md is not enough to be sure.`,

  frontend: `# Front-end judgement for generated pages

- Responsive by default: the page must work at ~400px wide. No fixed widths wider than the
  viewport, no horizontal body scroll.
- Respect the existing design. Read a neighbouring component before inventing a new visual
  language; matching what is there matters more than your preference.
- Accessibility is not optional: real heading order, labels tied to inputs, alt text, and colour
  contrast that survives a dark background.
- Prefer CSS to JavaScript for layout and animation.
- Placeholder copy should be plausible for the site's subject, never lorem ipsum.`,
};

/** The text of one skill, or null when the name is unknown. */
export function readSkill(name: string): string | null {
  return SKILLS[name] ?? null;
}
