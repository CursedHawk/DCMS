import { Bot, Globe, GitBranch, Image, KeyRound, ShieldCheck, Users } from 'lucide-react';
import { Footer, Header } from '../components/Layout';
import { HeroDemo } from '../components/HeroDemo';
import { Modes } from '../components/Modes';
import { faq } from '../faq';
import { CONSOLE_URL } from '../site';

const plugins = [
  {
    group: 'Publish',
    items: ['Blog', 'Articles', 'Events and line-ups', 'Team roster', 'File downloads'],
  },
  {
    group: 'Media',
    items: [
      'Image galleries',
      'Carousels',
      'Video galleries',
      'Adaptive video streaming',
      'Audio library',
    ],
  },
  {
    group: 'Visitors',
    items: [
      'Forms with email alerts',
      'Site search',
      'AI chatbot',
      'Visitor accounts',
      'Consent-based analytics',
    ],
  },
  {
    group: 'Connect',
    items: ['Instagram feed', 'Facebook Page feed', 'Branding and themes', 'Google Drive import'],
  },
];

const hosting = [
  {
    icon: Globe,
    title: 'Your domain, with HTTPS',
    body: 'Verify a domain with one DNS record. Certificates are issued and renewed for you, and the site is served from the edge the moment it is linked.',
  },
  {
    icon: GitBranch,
    title: 'Every change in git',
    body: 'Each site has its own repository with full history. Roll back, review a change, or clone the whole thing and leave.',
  },
  {
    icon: ShieldCheck,
    title: 'Workspaces kept apart',
    body: 'Every query is scoped to its workspace by the application and again by row-level security in PostgreSQL.',
  },
  {
    icon: Users,
    title: 'Roles for your team',
    body: 'Invite people and give them exactly the permissions they need, down to which site repositories they can push to.',
  },
  {
    icon: Image,
    title: 'Media handled for you',
    body: 'Images are resized to WebP, video is converted for adaptive streaming, and uploads are checked before they are stored.',
  },
  {
    icon: KeyRound,
    title: 'Secrets in a vault',
    body: 'API keys and credentials are encrypted in HashiCorp Vault, never kept in plain text alongside your content.',
  },
];

export function Home() {
  return (
    <>
      <Header />
      <main>
        {/* ---- Hero ---- */}
        <section className="relative overflow-hidden">
          <div className="dot-grid pointer-events-none absolute inset-0" aria-hidden="true" />
          <div className="relative mx-auto grid max-w-6xl items-center gap-12 px-4 pt-14 pb-20 sm:px-6 lg:grid-cols-[1.05fr_1fr] lg:gap-14 lg:pt-24 lg:pb-28">
            <div>
              <h1 className="text-[2.6rem] leading-[1.04] font-semibold tracking-[-0.035em] text-balance sm:text-6xl lg:text-[4.1rem]">
                The website builder and CMS that keeps every site in git.
              </h1>
              <p className="mt-6 max-w-[34rem] text-lg text-pretty text-muted-foreground sm:text-xl sm:leading-relaxed">
                Design pages visually, build in React, or let the AI agent write them. DCMS manages
                your content with plugins and publishes to your own domain, with HTTPS set up for
                you.
              </p>
              <div className="mt-9 flex flex-wrap items-center gap-3">
                <a
                  href={CONSOLE_URL}
                  className="rounded-full bg-primary px-6 py-3 font-medium text-primary-foreground shadow-[0_8px_24px_-8px_hsl(var(--primary)/0.7)] transition hover:brightness-110"
                >
                  Create an account
                </a>
                <a
                  href="#build"
                  className="rounded-full px-5 py-3 font-medium transition-colors hover:bg-secondary"
                >
                  See how it works
                </a>
              </div>
              <p className="mt-6 text-[0.9375rem] text-muted-foreground">
                Hosted in the EU. Admin in English and Czech.
              </p>
            </div>
            <HeroDemo />
          </div>
        </section>

        {/* ---- Ways to build ---- */}
        <section id="build" aria-labelledby="build-title" className="border-t bg-card/60">
          <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
            <div className="max-w-2xl">
              <h2
                id="build-title"
                className="text-3xl font-semibold tracking-tight text-balance sm:text-[2.6rem] sm:leading-tight"
              >
                Four ways to build, one place to publish
              </h2>
              <p className="mt-4 text-lg text-muted-foreground">
                Pick how you like to work for each site. They all share the same content, domains,
                team and hosting.
              </p>
            </div>
            <div className="mt-12">
              <Modes />
            </div>
          </div>
        </section>

        {/* ---- Plugins ---- */}
        <section id="plugins" aria-labelledby="plugins-title" className="border-t">
          <div className="mx-auto grid max-w-6xl gap-12 px-4 py-20 sm:px-6 lg:grid-cols-[1fr_2fr] lg:gap-16 lg:py-28">
            <div>
              <h2
                id="plugins-title"
                className="text-3xl font-semibold tracking-tight text-balance sm:text-[2.6rem] sm:leading-tight"
              >
                Content that has somewhere to live
              </h2>
              <p className="mt-4 text-lg text-muted-foreground">
                Turn on the plugins a site needs. Each one has its own screens in the admin, drafts
                and version history, and blocks you can place on a page.
              </p>
              <p className="mt-4 text-muted-foreground">
                Every workspace also gets a read-only content API with an OpenAPI document and a
                generated TypeScript client.
              </p>
            </div>
            <div className="grid grid-cols-2 gap-x-6 gap-y-10 sm:gap-x-10">
              {plugins.map((p) => (
                <div key={p.group}>
                  <h3 className="border-b pb-3 font-semibold">{p.group}</h3>
                  <ul className="mt-3 space-y-2 text-muted-foreground">
                    {p.items.map((i) => (
                      <li key={i}>{i}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          </div>
        </section>

        {/* ---- AI ---- */}
        <section id="ai" aria-labelledby="ai-title" className="border-t bg-card/60">
          <div className="mx-auto grid max-w-6xl items-center gap-12 px-4 py-20 sm:px-6 lg:grid-cols-2 lg:gap-16 lg:py-28">
            <div className="lg:order-2">
              <h2
                id="ai-title"
                className="text-3xl font-semibold tracking-tight text-balance sm:text-[2.6rem] sm:leading-tight"
              >
                An assistant that works inside your workspace
              </h2>
              <p className="mt-4 text-lg text-muted-foreground">
                Ask for a new page, a rewritten section or ten blog drafts. The assistant reads your
                sites and content, proposes the change, and asks before anything risky.
              </p>
              <ul className="mt-6 space-y-2.5">
                {[
                  'Use Anthropic, OpenAI, any OpenAI-compatible API, or a local model',
                  'Bring your own key, for yourself or the whole workspace',
                  'A chatbot for your visitors that answers from your published pages and hands over to a person',
                ].map((t) => (
                  <li key={t} className="flex gap-3">
                    <span
                      className="mt-[0.6em] size-1.5 shrink-0 rounded-full bg-primary"
                      aria-hidden="true"
                    />
                    {t}
                  </li>
                ))}
              </ul>
            </div>
            <figure
              className="rounded-2xl border bg-background p-5 shadow-sm lg:order-1"
              aria-label="Example conversation with the assistant"
            >
              <div className="ml-auto w-fit max-w-[85%] rounded-2xl rounded-br-md bg-primary px-4 py-2.5 text-[0.9375rem] text-primary-foreground">
                Add an events page that lists our next three concerts, newest first.
              </div>
              <div className="mt-4 flex gap-3">
                <span className="grid size-8 shrink-0 place-items-center rounded-full bg-accent text-accent-foreground">
                  <Bot className="size-4" aria-hidden="true" />
                </span>
                <div className="min-w-0 space-y-3 text-[0.9375rem]">
                  <p>
                    I’ll add a page at /events bound to the Events plugin, sorted by date, limited
                    to three.
                  </p>
                  <div className="rounded-lg border bg-secondary/50 px-3 py-2 font-mono text-[0.8rem] leading-6">
                    <div>
                      <span className="text-success">+</span> pages/events.json
                    </div>
                    <div>
                      <span className="text-warning">~</span> app.json{' '}
                      <span className="text-muted-foreground">(menu link)</span>
                    </div>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <span className="rounded-full bg-foreground px-3 py-1 text-sm text-background">
                      Apply changes
                    </span>
                    <span className="rounded-full border px-3 py-1 text-sm text-muted-foreground">
                      Show diff
                    </span>
                  </div>
                </div>
              </div>
            </figure>
          </div>
        </section>

        {/* ---- Hosting: the one dark band, in both themes ---- */}
        <section
          id="hosting"
          aria-labelledby="hosting-title"
          className="relative overflow-hidden bg-sidebar text-sidebar-foreground dark:border-y dark:bg-card"
        >
          <div
            className="pointer-events-none absolute -top-40 right-[-10%] size-[36rem] rounded-full bg-[radial-gradient(circle,hsl(var(--sidebar-accent)/0.22),transparent_65%)]"
            aria-hidden="true"
          />
          <div className="relative mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
            <div className="max-w-2xl">
              <h2
                id="hosting-title"
                className="text-3xl font-semibold tracking-tight text-balance sm:text-[2.6rem] sm:leading-tight"
              >
                Hosting you don’t have to think about
              </h2>
              <p className="mt-4 text-lg text-sidebar-muted">
                Domains, certificates, media and security are part of the platform, not a list of
                services to wire together.
              </p>
            </div>
            <dl className="mt-14 grid gap-x-12 gap-y-10 sm:grid-cols-2 lg:grid-cols-3">
              {hosting.map(({ icon: Icon, title, body }) => (
                <div key={title} className="border-t border-white/10 pt-6">
                  <dt className="flex items-center gap-3 font-semibold">
                    <Icon className="size-5 text-sidebar-accent" aria-hidden="true" />
                    {title}
                  </dt>
                  <dd className="mt-2 text-sidebar-muted">{body}</dd>
                </div>
              ))}
            </dl>
          </div>
        </section>

        {/* ---- FAQ ---- */}
        <section id="faq" aria-labelledby="faq-title">
          <div className="mx-auto grid max-w-6xl gap-10 px-4 py-20 sm:px-6 lg:grid-cols-[1fr_2fr] lg:gap-16 lg:py-28">
            <h2
              id="faq-title"
              className="text-3xl font-semibold tracking-tight sm:text-[2.6rem] sm:leading-tight"
            >
              Questions
            </h2>
            <div className="divide-y border-y">
              {faq.map(({ q, a }) => (
                <details key={q} className="group py-5 [&_summary::-webkit-details-marker]:hidden">
                  <summary className="flex cursor-pointer list-none items-center justify-between gap-6 text-lg font-medium">
                    {q}
                    <span
                      className="grid size-7 shrink-0 place-items-center rounded-full border text-muted-foreground transition-transform duration-200 group-open:rotate-45"
                      aria-hidden="true"
                    >
                      +
                    </span>
                  </summary>
                  <p className="mt-3 max-w-prose text-muted-foreground">{a}</p>
                </details>
              ))}
            </div>
          </div>
        </section>

        {/* ---- Closing call to action ---- */}
        <section className="px-4 pb-20 sm:px-6 lg:pb-28">
          <div className="relative mx-auto max-w-6xl overflow-hidden rounded-3xl bg-primary px-6 py-14 text-center text-primary-foreground sm:px-12 sm:py-20">
            <div
              className="dot-grid pointer-events-none absolute inset-0 opacity-60 invert"
              aria-hidden="true"
            />
            <h2 className="relative text-3xl font-semibold tracking-tight text-balance sm:text-5xl">
              Start with an empty workspace
            </h2>
            <p className="relative mx-auto mt-4 max-w-xl text-lg opacity-85">
              Create an account, add a site, and publish it at a dcms.highgeek.eu address today.
              Bring your domain when you are ready.
            </p>
            <a
              href={CONSOLE_URL}
              className="relative mt-8 inline-block rounded-full bg-background px-7 py-3 font-medium text-foreground transition hover:opacity-90"
            >
              Create an account
            </a>
          </div>
        </section>
      </main>
      <Footer />
    </>
  );
}
