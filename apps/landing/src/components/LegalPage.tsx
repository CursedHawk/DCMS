import type { ReactNode } from 'react';
import { LEGAL_UPDATED } from '../site';
import { Footer, Header } from './Layout';

export interface Section {
  id: string;
  title: string;
  body: ReactNode;
}

const updated = new Date(LEGAL_UPDATED).toLocaleDateString('en-GB', {
  day: 'numeric',
  month: 'long',
  year: 'numeric',
  timeZone: 'UTC',
});

export function LegalPage({
  title,
  intro,
  sections,
}: {
  title: string;
  intro: ReactNode;
  sections: Section[];
}) {
  return (
    <>
      <Header />
      <main className="mx-auto grid max-w-6xl gap-12 px-4 pt-14 pb-24 sm:px-6 lg:grid-cols-[15rem_1fr] lg:gap-16 lg:pt-20">
        <nav aria-label="On this page" className="hidden lg:block">
          <div className="sticky top-24">
            <p className="text-sm font-medium">On this page</p>
            <ol className="mt-3 space-y-1.5 border-l text-sm text-muted-foreground">
              {sections.map((s) => (
                <li key={s.id}>
                  <a
                    href={`#${s.id}`}
                    className="-ml-px block border-l border-transparent pl-4 hover:border-foreground hover:text-foreground"
                  >
                    {s.title}
                  </a>
                </li>
              ))}
            </ol>
          </div>
        </nav>
        <article className="legal min-w-0 max-w-[44rem]">
          <h1 className="text-4xl font-semibold tracking-[-0.03em] sm:text-5xl">{title}</h1>
          <p className="!mt-4 text-muted-foreground">
            Last updated <time dateTime={LEGAL_UPDATED}>{updated}</time>
          </p>
          <div className="mt-8 text-lg">{intro}</div>
          {sections.map((s, i) => (
            <section key={s.id} id={s.id} aria-labelledby={`${s.id}-h`}>
              <h2 id={`${s.id}-h`}>
                {i + 1}. {s.title}
              </h2>
              {s.body}
            </section>
          ))}
        </article>
      </main>
      <Footer />
    </>
  );
}
