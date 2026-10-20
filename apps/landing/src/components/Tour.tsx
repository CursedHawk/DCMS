import { useEffect, useRef, useState } from 'react';
import { Shot } from './Shot';

const steps = [
  {
    shot: 'builder',
    title: 'Design pages by dragging them together',
    body: 'Pick a section, drop it on the page and change the words. Headers, galleries, forms and sign-in come ready to place, and you see the result as you build it.',
    alt: 'The visual builder with a page hero selected and its settings open',
  },
  {
    shot: 'records',
    title: 'Build the app behind your website',
    body: 'Make your own tables for customers, bookings, orders or anything else. Search, filter and edit records like a spreadsheet, then show them on your site to the people allowed to see them.',
    alt: 'A table of customer accounts with filters for type, industry and segment',
  },
  {
    shot: 'automations',
    title: 'Let routine work run itself',
    body: 'When a form arrives or a deal is won, send the email, update the record or start the onboarding. Automations run in the background and keep a log of everything they did.',
    alt: 'A list of automations such as Opportunity won and Start onboarding',
  },
  {
    shot: 'forms',
    title: 'Every message in one inbox',
    body: 'Contact, signup and request forms collect in one place. Mark them handled, search them, or get an email the moment one arrives.',
    alt: 'The form submissions inbox listing three demo requests',
  },
  {
    shot: 'analytics',
    title: 'See who visits and what they read',
    body: 'Visitors, page views and where people came from, for every site, without a third-party tracker. Visitors are asked before anything is counted.',
    alt: 'The analytics dashboard with visitor counts and a page views chart',
  },
];

/**
 * Text scrolls, the screenshot stays: on wide screens the frame is sticky and shows the step
 * nearest the middle of the viewport. On narrow screens each step simply carries its own shot.
 */
export function Tour() {
  const [active, setActive] = useState(0);
  const list = useRef<HTMLOListElement>(null);

  useEffect(() => {
    const io = new IntersectionObserver(
      (entries) => {
        for (const e of entries)
          if (e.isIntersecting) setActive(Number((e.target as HTMLElement).dataset.step));
      },
      { rootMargin: '-45% 0px -45% 0px' },
    );
    list.current?.querySelectorAll('[data-step]').forEach((el) => io.observe(el));
    return () => io.disconnect();
  }, []);

  return (
    <div className="grid gap-12 lg:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] lg:gap-16">
      <ol ref={list} className="space-y-20 lg:space-y-0">
        {steps.map((s, i) => (
          <li key={s.shot} data-step={i} className="lg:flex lg:min-h-[78vh] lg:items-center">
            <div
              className={`transition-opacity duration-500 lg:max-w-md ${i === active ? 'lg:opacity-100' : 'lg:opacity-35'}`}
            >
              <h3 className="text-2xl font-semibold tracking-tight text-balance sm:text-3xl">
                {s.title}
              </h3>
              <p className="mt-4 text-lg text-pretty text-muted-foreground">{s.body}</p>
            </div>
            <Shot name={s.shot} alt={s.alt} className="mt-8 lg:hidden" />
          </li>
        ))}
      </ol>
      <div className="hidden lg:block">
        <div className="sticky top-[calc(50vh-17rem)]">
          <div className="relative aspect-[16/10]">
            {steps.map((s, i) => (
              <Shot
                key={s.shot}
                name={s.shot}
                alt={s.alt}
                hidden={i !== active}
                className={`absolute inset-0 transition-[opacity,transform] duration-700 ease-out ${
                  i === active ? 'opacity-100' : 'pointer-events-none scale-[0.97] opacity-0'
                }`}
              />
            ))}
          </div>
          <div className="mt-6 flex justify-center gap-2" aria-hidden="true">
            {steps.map((s, i) => (
              <span
                key={s.shot}
                className={`h-1.5 rounded-full transition-all duration-500 ${i === active ? 'w-6 bg-primary' : 'w-1.5 bg-foreground/20'}`}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
