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

- Entry: \`src/main.tsx\` renders into \`#root\` from \`index.html\`. Never break that wiring.
- Routing: React Router, if the site uses it. The preview runs the app inside an \`about:srcdoc\`
  iframe, where browser history is not usable — the preview bundler swaps \`createBrowserRouter\`
  for an in-memory router pinned to "/". So a route you add works in production and may not be
  reachable by clicking in the preview. Verify it by reading the code, not only by clicking.
- Styling: plain CSS or Tailwind. Tailwind is compiled in-browser for the preview and by the real
  build for production, so utility classes behave the same in both.
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

- The site reads content from the tenant's delivery API. A generated typed client may already
  exist under \`src/api\` — search there before writing \`fetch\` by hand.
- In the preview, API calls are proxied same-origin and forced into the tenant SANDBOX, so
  preview writes never touch live content. In production they hit live content.
- Use \`sandbox_request\` to check an endpoint exists and see its real shape before you write
  code against it. Guessing at a response shape is the most common cause of a page that builds
  and then renders nothing.
- Content types vary per tenant. \`describe_content_types\` tells you what this one has.`,

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
