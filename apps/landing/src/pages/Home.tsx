import { Bot, Code2, Globe, History, ShieldCheck, Smartphone, Users } from 'lucide-react';
import { Footer, Header } from '../components/Layout';
import { Plugins } from '../components/Plugins';
import { Shot } from '../components/Shot';
import { Tour } from '../components/Tour';
import { faq } from '../faq';
import { CONSOLE_URL } from '../site';

const included = [
  {
    icon: Globe,
    title: 'Your own domain',
    body: 'Connect a domain you own and the secure connection is set up and renewed for you. Until then, every site has a free address.',
  },
  {
    icon: Users,
    title: 'Your whole team',
    body: 'Invite people and choose exactly what each of them can see and change.',
  },
  {
    icon: History,
    title: 'Nothing gets lost',
    body: 'Every version of every page is kept, so you can look back or undo at any time.',
  },
  {
    icon: Smartphone,
    title: 'Works on your phone',
    body: 'Edit content, check visitors and answer messages from any device.',
  },
  {
    icon: Code2,
    title: 'Room for developers',
    body: 'Prefer code? Open the same site in the built-in code editor, or in your own tools.',
  },
  {
    icon: ShieldCheck,
    title: 'Hosted in the EU',
    body: 'Your data stays on servers in the European Union, kept apart from every other workspace.',
  },
];

const h2 =
  'text-3xl font-semibold tracking-[-0.025em] text-balance sm:text-[2.75rem] sm:leading-[1.1]';

export function Home() {
  return (
    <>
      <Header />
      <main>
        {/* ---- Hero: the product, tilted back, settling flat as you scroll ---- */}
        <section className="relative overflow-hidden">
          <div
            className="pointer-events-none absolute inset-x-0 top-[22rem] h-[48rem] bg-[radial-gradient(ellipse_at_center,hsl(var(--primary)/0.22),transparent_65%)]"
            aria-hidden="true"
          />
          <div className="relative mx-auto max-w-6xl px-4 pt-16 text-center sm:px-6 lg:pt-24">
            <h1 className="hero-in mx-auto max-w-4xl text-[2.75rem] leading-[1.02] font-semibold tracking-[-0.045em] text-balance sm:text-7xl lg:text-[5.5rem]">
              Build the website. Run everything behind it.
            </h1>
            <p
              className="hero-in mx-auto mt-7 max-w-2xl text-lg text-pretty text-muted-foreground sm:text-xl sm:leading-relaxed"
              style={{ animationDelay: '120ms' }}
            >
              DCMS is a website builder, content manager and app platform in one. Drag pages
              together, add a blog, gallery, forms or a members’ area, and publish on your own
              domain.
            </p>
            <div
              className="hero-in mt-10 flex flex-wrap items-center justify-center gap-3"
              style={{ animationDelay: '240ms' }}
            >
              <a
                href={CONSOLE_URL}
                className="rounded-full bg-primary px-7 py-3.5 font-medium text-primary-foreground shadow-[0_10px_30px_-10px_hsl(var(--primary)/0.8)] transition hover:brightness-110"
              >
                Create an account
              </a>
              <a
                href="#features"
                className="rounded-full px-6 py-3.5 font-medium transition-colors hover:bg-secondary"
              >
                See what it does
              </a>
            </div>
            <p
              className="hero-in mt-6 text-[0.9375rem] text-muted-foreground"
              style={{ animationDelay: '320ms' }}
            >
              Free to start. Hosted in the EU. Admin in English and Czech.
            </p>
          </div>
          <div className="hero-stage relative mx-auto mt-16 max-w-6xl px-4 pb-20 sm:px-6 lg:mt-20 lg:pb-32">
            <Shot
              name="builder"
              alt="The DCMS visual builder: a page with its hero heading selected, component library on the left and settings on the right"
              className="hero-tilt"
            />
          </div>
        </section>

        {/* ---- Feature tour ---- */}
        <section id="features" aria-labelledby="features-title" className="border-t">
          <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
            <div className="max-w-2xl">
              <h2 id="features-title" className={h2}>
                One place for your site and everything around it
              </h2>
              <p className="mt-5 text-lg text-muted-foreground">
                Pages, content, customers, messages and numbers live side by side, so you are not
                stitching five services together.
              </p>
            </div>
            <div className="mt-16 lg:mt-4">
              <Tour />
            </div>
          </div>
        </section>

        {/* ---- Included: the one dark band, in both themes ---- */}
        <section
          id="included"
          aria-labelledby="included-title"
          className="relative overflow-hidden bg-sidebar text-sidebar-foreground dark:border-y dark:bg-card"
        >
          <div
            className="pointer-events-none absolute -top-48 left-1/2 size-[44rem] -translate-x-1/2 rounded-full bg-[radial-gradient(circle,hsl(var(--sidebar-accent)/0.25),transparent_65%)]"
            aria-hidden="true"
          />
          <div className="relative mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
            <h2 id="included-title" className={`${h2} max-w-2xl`}>
              The rest is taken care of
            </h2>
            <dl className="mt-14 grid gap-x-12 gap-y-12 sm:grid-cols-2 lg:grid-cols-3">
              {included.map(({ icon: Icon, title, body }) => (
                <div key={title} className="reveal">
                  <dt className="font-semibold">
                    <span className="mb-4 grid size-11 place-items-center rounded-xl bg-white/10">
                      <Icon className="size-5 text-sidebar-accent" aria-hidden="true" />
                    </span>
                    {title}
                  </dt>
                  <dd className="mt-2 text-sidebar-muted">{body}</dd>
                </div>
              ))}
            </dl>
          </div>
        </section>

        {/* ---- AI ---- */}
        <section id="ai" aria-labelledby="ai-title">
          <div className="mx-auto grid max-w-6xl items-center gap-12 px-4 py-20 sm:px-6 lg:grid-cols-2 lg:gap-20 lg:py-28">
            <div>
              <h2 id="ai-title" className={h2}>
                An assistant that knows your workspace
              </h2>
              <p className="mt-5 text-lg text-muted-foreground">
                Ask for a new page, a rewritten section or ten blog drafts. The assistant shows you
                what it wants to change and waits for your go-ahead.
              </p>
              <ul className="mt-7 space-y-3">
                {[
                  'Works with Anthropic, OpenAI or a model you run yourself',
                  'Use your own key, for yourself or the whole team',
                  'Put a chatbot on your site that answers from your own pages',
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
              className="reveal rounded-3xl border bg-card p-5 sm:p-7"
              aria-label="Example conversation with the assistant"
            >
              <p className="ml-auto w-fit max-w-[85%] rounded-2xl rounded-br-md bg-primary px-4 py-2.5 text-[0.9375rem] text-primary-foreground">
                Add an events page with our next three concerts.
              </p>
              <div className="mt-5 flex gap-3">
                <span className="grid size-8 shrink-0 place-items-center rounded-full bg-accent text-accent-foreground">
                  <Bot className="size-4" aria-hidden="true" />
                </span>
                <div className="min-w-0 flex-1 space-y-4 text-[0.9375rem]">
                  <p>
                    Here’s a draft. It lists the next three concerts, newest first, and I added it
                    to the menu.
                  </p>
                  <div className="space-y-2 rounded-xl border bg-background p-3">
                    {[
                      ['24', 'Oct', 'Autumn concert'],
                      ['15', 'Nov', 'Strings and voices'],
                      ['06', 'Dec', 'Christmas gala'],
                    ].map(([d, m, t]) => (
                      <div key={t} className="flex items-center gap-3">
                        <span className="grid w-10 place-items-center rounded-md bg-accent py-1 leading-none text-accent-foreground">
                          <span className="text-sm font-semibold">{d}</span>
                          <span className="text-[0.65rem]">{m}</span>
                        </span>
                        <span className="font-medium">{t}</span>
                      </div>
                    ))}
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <span className="rounded-full bg-foreground px-3.5 py-1.5 text-sm text-background">
                      Apply changes
                    </span>
                    <span className="rounded-full border px-3.5 py-1.5 text-sm text-muted-foreground">
                      Show changes
                    </span>
                  </div>
                </div>
              </div>
            </figure>
          </div>
        </section>

        {/* ---- Plugins ---- */}
        <section id="plugins" aria-labelledby="plugins-title" className="border-t bg-card/60">
          <div className="mx-auto max-w-6xl px-4 py-20 sm:px-6 lg:py-28">
            <div className="grid gap-6 lg:grid-cols-2 lg:items-end lg:gap-16">
              <h2 id="plugins-title" className={h2}>
                Add what your site needs, when it needs it
              </h2>
              <p className="text-lg text-muted-foreground">
                Plugins switch on from the marketplace in a click. Each one brings its own screens
                to the admin and its own sections to place on your pages.
              </p>
            </div>
            <div className="mt-14">
              <Plugins />
            </div>
            <div className="mt-20">
              <Shot
                name="marketplace"
                alt="The plugin marketplace in the admin, listing plugins by category"
                className="reveal"
              />
            </div>
          </div>
        </section>

        {/* ---- FAQ ---- */}
        <section id="faq" aria-labelledby="faq-title" className="border-t">
          <div className="mx-auto grid max-w-6xl gap-10 px-4 py-20 sm:px-6 lg:grid-cols-[1fr_2fr] lg:gap-16 lg:py-28">
            <h2 id="faq-title" className={h2}>
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
          <div className="reveal relative mx-auto max-w-6xl overflow-hidden rounded-3xl bg-primary px-6 py-16 text-center text-primary-foreground sm:px-12 sm:py-24">
            <div
              className="pointer-events-none absolute -bottom-40 left-1/2 size-[40rem] -translate-x-1/2 rounded-full bg-[radial-gradient(circle,rgb(255_255_255/0.22),transparent_65%)]"
              aria-hidden="true"
            />
            <h2 className="relative text-3xl font-semibold tracking-[-0.03em] text-balance sm:text-5xl">
              Your first site is a few minutes away
            </h2>
            <p className="relative mx-auto mt-5 max-w-xl text-lg opacity-85">
              Create an account, pick a starting point and publish at a free address today. Bring
              your own domain whenever you are ready.
            </p>
            <a
              href={CONSOLE_URL}
              className="relative mt-9 inline-block rounded-full bg-background px-7 py-3.5 font-medium text-foreground transition hover:opacity-90"
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
