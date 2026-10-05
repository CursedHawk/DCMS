import { useState } from 'react';

const modes = [
  {
    id: 'visual',
    tab: 'Visual editor',
    title: 'Drag blocks onto a page',
    body: 'Lay out pages with a drag-and-drop editor and style them without writing code. What you save is plain HTML and CSS in the site’s git repository, so nothing about the result is proprietary.',
    facts: [
      'Publishes in about a second',
      'Blocks for galleries, forms, events and more',
      'Edit the generated HTML by hand whenever you like',
    ],
    files: ['index.html', 'about.html', 'styles.css', 'assets/logo.svg'],
  },
  {
    id: 'react',
    tab: 'React builder',
    title: 'Build a React app visually',
    body: 'Compose pages from components, routes and a shared app shell, and bind them to your content: blog posts, events, galleries. The published site is a real React app built from what you assembled.',
    facts: [
      'Reusable components and page routes',
      'Data bound from your plugins',
      'Forms and actions without a backend',
    ],
    files: ['app.json', 'pages/home.json', 'pages/events.json', 'components/Hero.json'],
  },
  {
    id: 'code',
    tab: 'Code',
    title: 'Write it yourself, with help',
    body: 'Open the project in the browser IDE with a live preview, or clone it and use your own editor. An AI coding agent can work on it beside you. Push to the release branch and the site builds in an isolated sandbox.',
    facts: [
      'React and Vite, nothing custom',
      'Clone and push over HTTPS or SSH',
      'Builds in about 30 seconds',
    ],
    files: ['package.json', 'src/main.tsx', 'src/App.tsx', 'src/pages/Blog.tsx'],
  },
  {
    id: 'upload',
    tab: 'Upload',
    title: 'Already have a site? Upload it',
    body: 'Bring a static site exported from anywhere else. Upload a zip or a folder and DCMS serves it as it is, on your domain, with the same certificates and hosting as everything else.',
    facts: [
      'Zip or folder upload',
      'Live in under half a second',
      'Swap it for a built site later',
    ],
    files: ['site.zip'],
  },
];

export function Modes() {
  const [active, setActive] = useState(modes[0].id);

  const onKeyDown = (e: React.KeyboardEvent) => {
    const i = modes.findIndex((m) => m.id === active);
    const next = e.key === 'ArrowRight' ? i + 1 : e.key === 'ArrowLeft' ? i - 1 : null;
    if (next === null) return;
    const id = modes[(next + modes.length) % modes.length].id;
    setActive(id);
    document.getElementById(`tab-${id}`)?.focus();
  };

  return (
    <div>
      <div
        role="tablist"
        aria-label="Ways to build a site"
        className="inline-flex max-w-full gap-1 overflow-x-auto rounded-full border bg-secondary/60 p-1"
      >
        {modes.map((m) => (
          <button
            key={m.id}
            id={`tab-${m.id}`}
            role="tab"
            type="button"
            aria-selected={m.id === active}
            aria-controls={`panel-${m.id}`}
            tabIndex={m.id === active ? 0 : -1}
            onClick={() => setActive(m.id)}
            onKeyDown={onKeyDown}
            className="shrink-0 rounded-full px-4 py-2 text-[0.9375rem] font-medium text-muted-foreground transition-colors hover:text-foreground aria-selected:bg-background aria-selected:text-foreground aria-selected:shadow-sm"
          >
            {m.tab}
          </button>
        ))}
      </div>

      {/* Every panel is in the HTML, so crawlers read all four; inactive ones are only hidden. */}
      {modes.map((m) => (
        <div
          key={m.id}
          id={`panel-${m.id}`}
          role="tabpanel"
          aria-labelledby={`tab-${m.id}`}
          hidden={m.id !== active}
          className="mt-10 grid gap-10 md:grid-cols-[1.15fr_1fr] md:gap-16"
        >
          <div>
            <h3 className="text-2xl font-semibold tracking-tight">{m.title}</h3>
            <p className="mt-4 max-w-prose text-muted-foreground">{m.body}</p>
            <ul className="mt-6 space-y-2.5">
              {m.facts.map((f) => (
                <li key={f} className="flex gap-3">
                  <span
                    className="mt-[0.6em] size-1.5 shrink-0 rounded-full bg-primary"
                    aria-hidden="true"
                  />
                  {f}
                </li>
              ))}
            </ul>
          </div>
          <figure className="self-start rounded-xl border bg-card p-5 animate-[dcms-fade-in_.35s_ease-out]">
            <figcaption className="text-sm text-muted-foreground">In the repository</figcaption>
            <ul className="mt-3 space-y-1 font-mono text-sm">
              {m.files.map((f) => (
                <li key={f}>
                  <span className="text-muted-foreground">
                    {f.slice(0, f.lastIndexOf('/') + 1)}
                  </span>
                  {f.slice(f.lastIndexOf('/') + 1)}
                </li>
              ))}
            </ul>
          </figure>
        </div>
      ))}
    </div>
  );
}
