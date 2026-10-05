import { useEffect, useId, useRef, useState, useSyncExternalStore } from 'react';
import { IconGlyph } from './contentComponents';
import { isSafeExternalHref } from './ids';
import { choice, select, text, type Props } from './kit';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';

/**
 * Interactive primitives (Mode D v2, U1.3): accordion, tabs, carousel, countdown, social links.
 *
 * On the canvas every part is shown open — every answer, every tab panel, every slide in a row —
 * because an author edits what they can see; the behaviour is the site's.
 */

const noSubscribe = () => () => {};
/** False on the server and while hydrating, so a prerendered page and its first render agree. */
function useInBrowser(): boolean {
  return useSyncExternalStore(noSubscribe, () => true, () => false);
}

// ---------------------------------------------------------------------------
// Accordion — native <details>, so it works and is accessible before any script runs.
// ---------------------------------------------------------------------------

const ACCORDION_LOOKS = ['lines', 'boxed', 'plain'] as const;

function Accordion({ nodeId, props, slot }: ComponentRenderProps<Props>) {
  const look = choice(props.look, ACCORDION_LOOKS, 'lines');
  return (
    <div id={`dcms-accordion-${nodeId}`} className={`dcms-accordion dcms-accordion-${look}`} data-single={props.single === true ? 'true' : undefined}>
      {slot('items', { className: 'dcms-accordion-items' })}
    </div>
  );
}

function AccordionItem({ props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const group = useRef<HTMLDetailsElement>(null);
  // "One open at a time" is the parent's setting, read from the page: details sharing a name are
  // exclusive. Never on the canvas, where every answer stays open to be edited.
  useEffect(() => {
    const el = group.current;
    const accordion = el?.closest('.dcms-accordion');
    if (!el || !accordion || mode === 'edit') return;
    if (accordion.getAttribute('data-single') === 'true') el.setAttribute('name', accordion.id);
    else el.removeAttribute('name');
  });
  return (
    <details ref={group} className="dcms-accordion-item" open={mode === 'edit' || props.open === true}>
      <summary>
        <span>{text(props.title, 'A question people ask')}</span>
        <IconGlyph name="chevron-down" className="dcms-accordion-chevron" />
      </summary>
      {slot('default', { className: 'dcms-accordion-body' })}
    </details>
  );
}

// ---------------------------------------------------------------------------
// Tabs — panels are children; the tab bar is built from their labels on the site.
// ---------------------------------------------------------------------------

function Tabs({ props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const inBrowser = useInBrowser();
  const box = useRef<HTMLDivElement>(null);
  const [labels, setLabels] = useState<string[]>([]);
  const [active, setActive] = useState(0);
  const id = useId();
  const look = choice(props.look, ['underline', 'pills'] as const, 'underline');

  // The panels render themselves; read their labels and show only the active one. Runs after
  // every render on purpose (panels come and go); setLabels only changes state when they differ.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    // Its own panels only — not those of a tab set nested inside one of them.
    const panels = [...(box.current?.querySelectorAll<HTMLElement>('[data-dcms-tab]') ?? [])].filter((p) => p.closest('.dcms-tabs') === box.current);
    const next = panels.map((p) => p.dataset.dcmsTab ?? '');
    setLabels((old) => (old.join('\u0000') === next.join('\u0000') ? old : next));
    if (mode === 'edit') return;
    panels.forEach((panel, i) => {
      panel.hidden = i !== active;
      panel.id = `${id}-panel-${i}`;
      panel.setAttribute('role', 'tabpanel');
      panel.setAttribute('aria-labelledby', `${id}-tab-${i}`);
    });
  });

  const pick = (i: number) => {
    setActive(i);
    box.current?.ownerDocument.getElementById(`${id}-tab-${i}`)?.focus();
  };

  return (
    <div ref={box} className={`dcms-tabs dcms-tabs-${look}`}>
      {mode === 'live' && inBrowser && labels.length > 0 && (
        <div role="tablist" className="dcms-tabs-bar">
          {labels.map((label, i) => (
            <button
              key={i}
              id={`${id}-tab-${i}`}
              type="button"
              role="tab"
              aria-selected={i === active}
              aria-controls={`${id}-panel-${i}`}
              tabIndex={i === active ? 0 : -1}
              onClick={() => setActive(i)}
              onKeyDown={(e) => {
                if (e.key === 'ArrowRight') pick((i + 1) % labels.length);
                else if (e.key === 'ArrowLeft') pick((i - 1 + labels.length) % labels.length);
              }}
            >
              {label}
            </button>
          ))}
        </div>
      )}
      {slot('tabs', { className: 'dcms-tabs-panels' })}
    </div>
  );
}

function Tab({ props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const label = text(props.label, 'Tab');
  return (
    <section className="dcms-tab" data-dcms-tab={label}>
      {/* On the canvas, and before the script runs, each panel names itself. */}
      {mode === 'edit' && <div className="dcms-tab-label">{label}</div>}
      {slot('default', { className: 'dcms-tab-body' })}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Carousel — a scroll-snap track; arrows and dots move it, autoplay respects reduced motion.
// ---------------------------------------------------------------------------

const PER_VIEW = ['1', '2', '3'] as const;

function Carousel({ props, slot }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const box = useRef<HTMLDivElement>(null);
  const [count, setCount] = useState(0);
  const [index, setIndex] = useState(0);
  const perView = choice(props.perView, PER_VIEW, '1');
  const autoplay = mode === 'live' && props.autoplay === true;
  const track = () => box.current?.querySelector<HTMLElement>('.dcms-carousel-track');

  // After every render on purpose: slides come and go. setCount only changes state when they did.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    const t = track();
    if (!t) return;
    const slides = t.children.length;
    setCount((c) => (c === slides ? c : slides));
    const onScroll = () => setIndex(Math.round(t.scrollLeft / Math.max(1, t.clientWidth / Number(perView))));
    t.addEventListener('scroll', onScroll, { passive: true });
    return () => t.removeEventListener('scroll', onScroll);
  });

  const go = (to: number) => {
    const t = track();
    if (!t || count === 0) return;
    const next = (to + count) % count;
    t.scrollTo({ left: (t.clientWidth / Number(perView)) * next, behavior: 'smooth' });
  };

  useEffect(() => {
    if (!autoplay || count < 2 || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return;
    const el = box.current;
    let paused = false;
    const pause = () => (paused = true);
    const resume = () => (paused = false);
    el?.addEventListener('pointerenter', pause);
    el?.addEventListener('pointerleave', resume);
    el?.addEventListener('focusin', pause);
    el?.addEventListener('focusout', resume);
    const timer = setInterval(() => !paused && go(index + 1), 5000);
    return () => {
      clearInterval(timer);
      el?.removeEventListener('pointerenter', pause);
      el?.removeEventListener('pointerleave', resume);
      el?.removeEventListener('focusin', pause);
      el?.removeEventListener('focusout', resume);
    };
  });

  return (
    <section ref={box} className={`dcms-carousel dcms-carousel-${perView}`} aria-roledescription="carousel" aria-label={text(props.label, 'Slides')}>
      {slot('slides', { className: 'dcms-carousel-track' })}
      {mode === 'live' && count > 1 && (
        <div className="dcms-carousel-controls">
          <button type="button" className="dcms-carousel-arrow" aria-label="Previous slide" onClick={() => go(index - 1)}>
            <IconGlyph name="arrow-left" />
          </button>
          <div className="dcms-carousel-dots">
            {Array.from({ length: count }, (_, i) => (
              <button key={i} type="button" aria-label={`Slide ${i + 1} of ${count}`} aria-current={i === index} onClick={() => go(i)} />
            ))}
          </div>
          <button type="button" className="dcms-carousel-arrow" aria-label="Next slide" onClick={() => go(index + 1)}>
            <IconGlyph name="arrow-right" />
          </button>
        </div>
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Countdown
// ---------------------------------------------------------------------------

/** Whole days, hours, minutes and seconds from `now` until `deadline`; null once it has passed. */
export function timeLeft(deadline: string, now: number): { days: number; hours: number; minutes: number; seconds: number } | null {
  const end = Date.parse(deadline);
  if (Number.isNaN(end) || end <= now) return null;
  let s = Math.floor((end - now) / 1000);
  const days = Math.floor(s / 86400);
  s -= days * 86400;
  const hours = Math.floor(s / 3600);
  s -= hours * 3600;
  const minutes = Math.floor(s / 60);
  return { days, hours, minutes, seconds: s - minutes * 60 };
}

function Countdown({ props }: ComponentRenderProps<Props>) {
  const inBrowser = useInBrowser();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const deadline = text(props.deadline);
  const left = inBrowser ? timeLeft(deadline, now) : null;
  if (inBrowser && !left) return <p className="dcms-countdown-done">{text(props.expiredText, 'It has started!')}</p>;
  const parts: [number | null, string][] = [
    [left?.days ?? null, text(props.daysLabel, 'days')],
    [left?.hours ?? null, text(props.hoursLabel, 'hours')],
    [left?.minutes ?? null, text(props.minutesLabel, 'minutes')],
    [left?.seconds ?? null, text(props.secondsLabel, 'seconds')],
  ];
  return (
    <div className="dcms-countdown" role="timer" aria-live="off">
      {parts.map(([value, label]) => (
        <div key={label} className="dcms-countdown-part">
          <span className="dcms-countdown-value">{value === null ? '–' : String(value).padStart(2, '0')}</span>
          <span className="dcms-countdown-label">{label}</span>
        </div>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Social links
// ---------------------------------------------------------------------------

export const SOCIAL_NETWORKS = [
  { prop: 'facebook', label: 'Facebook', icon: 'facebook' },
  { prop: 'instagram', label: 'Instagram', icon: 'instagram' },
  { prop: 'linkedin', label: 'LinkedIn', icon: 'linkedin' },
  { prop: 'youtube', label: 'YouTube', icon: 'youtube' },
  { prop: 'x', label: 'X (Twitter)', icon: 'twitter' },
  { prop: 'github', label: 'GitHub', icon: 'github' },
  { prop: 'email', label: 'Email', icon: 'mail' },
] as const;

const SOCIAL_LOOKS = ['icons', 'circles', 'labels'] as const;

function SocialLinks({ props }: ComponentRenderProps<Props>) {
  const mode = useRenderMode();
  const look = choice(props.look, SOCIAL_LOOKS, 'circles');
  const links = SOCIAL_NETWORKS.map((n) => {
    const raw = text(props[n.prop]).trim();
    const href = n.prop === 'email' && raw && !raw.startsWith('mailto:') ? `mailto:${raw}` : raw;
    return { ...n, href: href && isSafeExternalHref(href) ? href : '' };
  }).filter((n) => n.href);
  if (!links.length) return mode === 'edit' ? <p className="dcms-editor-note">Social links — add your profile addresses in the settings.</p> : null;
  return (
    <ul className={`dcms-social dcms-social-${look}`}>
      {links.map((n) => (
        <li key={n.prop}>
          {mode === 'live' ? (
            <a href={n.href} target="_blank" rel="noopener noreferrer me" aria-label={look === 'labels' ? undefined : n.label}>
              <IconGlyph name={n.icon} />
              {look === 'labels' && <span>{n.label}</span>}
            </a>
          ) : (
            // On the canvas a link goes nowhere: a click there selects.
            <span className="dcms-social-link" aria-label={look === 'labels' ? undefined : n.label}>
              <IconGlyph name={n.icon} />
              {look === 'labels' && <span>{n.label}</span>}
            </span>
          )}
        </li>
      ))}
    </ul>
  );
}

// ---------------------------------------------------------------------------

export const INTERACTIVE_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.accordion',
    version: 1,
    label: 'Accordion',
    description: 'Questions and answers that open when clicked — an FAQ that does not fill the page.',
    category: 'Interactive',
    keywords: ['faq', 'questions', 'collapse', 'expand', 'toggle', 'details'],
    component: Accordion,
    props: [
      select('look', 'Look', ACCORDION_LOOKS, 'lines', { lines: 'Lines between', boxed: 'In boxes', plain: 'Plain' }, {
        group: 'style',
        description: 'How the questions are separated.',
      }),
      {
        kind: 'boolean',
        name: 'single',
        label: 'One open at a time',
        default: false,
        group: 'behaviour',
        description: 'Opening a question closes the one that was open.',
      },
    ],
    slots: [{ name: 'items', label: 'Questions', allowed: ['dcms.accordion-item'] }],
    starter: {
      items: [
        { type: 'dcms.accordion-item', props: { title: 'How long does delivery take?' }, slots: { default: [{ type: 'dcms.text', props: { text: 'Usually two to three working days.' } }] } },
        { type: 'dcms.accordion-item', props: { title: 'Can I return an order?' }, slots: { default: [{ type: 'dcms.text', props: { text: 'Yes — within 30 days, no questions asked.' } }] } },
      ],
    },
  },
  {
    type: 'dcms.accordion-item',
    version: 1,
    label: 'Question',
    description: 'One question in an accordion, with its answer inside.',
    category: 'Interactive',
    keywords: ['faq item', 'answer', 'question'],
    component: AccordionItem,
    allowedParents: ['dcms.accordion'],
    props: [
      { kind: 'text', name: 'title', label: 'Question', default: 'A question people ask', maxLength: 200, group: 'content', description: 'What visitors click to open the answer.' },
      { kind: 'boolean', name: 'open', label: 'Open at first', default: false, group: 'behaviour', description: 'Show the answer when the page loads.' },
    ],
    slots: [{ name: 'default', label: 'Answer' }],
    starter: { default: [{ type: 'dcms.text', props: { text: 'The answer.' } }] },
  },
  {
    type: 'dcms.tabs',
    version: 1,
    label: 'Tabs',
    description: 'Several panels in the same place, one shown at a time — pick a tab to switch.',
    category: 'Interactive',
    keywords: ['tabbed', 'panels', 'switch', 'sections', 'pricing tabs'],
    component: Tabs,
    props: [
      select('look', 'Look', ['underline', 'pills'] as const, 'underline', { underline: 'Underlined', pills: 'Pills' }, {
        group: 'style',
        description: 'How the tab buttons look.',
      }),
    ],
    slots: [{ name: 'tabs', label: 'Tabs', allowed: ['dcms.tab'] }],
    starter: {
      tabs: [
        { type: 'dcms.tab', props: { label: 'Overview' }, slots: { default: [{ type: 'dcms.text', props: { text: 'What it is, in a sentence or two.' } }] } },
        { type: 'dcms.tab', props: { label: 'Details' }, slots: { default: [{ type: 'dcms.text', props: { text: 'The finer points.' } }] } },
        { type: 'dcms.tab', props: { label: 'Prices' }, slots: { default: [{ type: 'dcms.text', props: { text: 'What it costs.' } }] } },
      ],
    },
  },
  {
    type: 'dcms.tab',
    version: 1,
    label: 'Tab',
    description: 'One panel of a set of tabs, with the label on its button.',
    category: 'Interactive',
    keywords: ['tab panel', 'panel'],
    component: Tab,
    allowedParents: ['dcms.tabs'],
    props: [{ kind: 'text', name: 'label', label: 'Label', default: 'Tab', maxLength: 60, group: 'content', description: 'The text on this tab’s button.' }],
    slots: [{ name: 'default', label: 'Content' }],
    starter: { default: [{ type: 'dcms.text', props: { text: 'This tab’s content.' } }] },
  },
  {
    type: 'dcms.carousel',
    version: 1,
    label: 'Carousel',
    description: 'Slides visitors swipe or click through — pictures, quotes or cards, one or a few at a time.',
    category: 'Interactive',
    keywords: ['slider', 'slideshow', 'swiper', 'rotating', 'testimonials'],
    component: Carousel,
    props: [
      select('perView', 'Slides at once', PER_VIEW, '1', { '1': 'One', '2': 'Two', '3': 'Three' }, {
        group: 'layout',
        description: 'How many slides show side by side. Phones always show one.',
      }),
      {
        kind: 'boolean',
        name: 'autoplay',
        label: 'Move on by itself',
        default: false,
        group: 'behaviour',
        description: 'Advance every five seconds, pausing while a visitor points at it — and never for visitors who asked for less motion.',
      },
      { kind: 'text', name: 'label', label: 'Name (for screen readers)', default: 'Slides', maxLength: 80, group: 'content', description: 'What the slides are, for people who cannot see them.' },
    ],
    slots: [{ name: 'slides', label: 'Slides' }],
    interaction: 'interactive',
    starter: {
      slides: [
        { type: 'dcms.quote', props: { text: 'Exactly what we needed.', cite: 'A happy customer', style: 'card' } },
        { type: 'dcms.quote', props: { text: 'Fast, friendly and fair.', cite: 'Another happy customer', style: 'card' } },
      ],
    },
  },
  {
    type: 'dcms.countdown',
    version: 1,
    label: 'Countdown',
    description: 'Days, hours, minutes and seconds until a date — a launch, an event, the end of a sale.',
    category: 'Interactive',
    keywords: ['timer', 'launch', 'deadline', 'event', 'clock'],
    component: Countdown,
    props: [
      { kind: 'date', name: 'deadline', label: 'Counts down to', group: 'content', description: 'The date (and time) it reaches zero.' },
      { kind: 'text', name: 'expiredText', label: 'Afterwards it says', default: 'It has started!', maxLength: 120, group: 'content', description: 'Shown once the date has passed.' },
      { kind: 'text', name: 'daysLabel', label: '“Days” label', default: 'days', maxLength: 20, group: 'content', description: 'The word under the days, in your site’s language.' },
      { kind: 'text', name: 'hoursLabel', label: '“Hours” label', default: 'hours', maxLength: 20, group: 'content', description: 'The word under the hours.' },
      { kind: 'text', name: 'minutesLabel', label: '“Minutes” label', default: 'minutes', maxLength: 20, group: 'content', description: 'The word under the minutes.' },
      { kind: 'text', name: 'secondsLabel', label: '“Seconds” label', default: 'seconds', maxLength: 20, group: 'content', description: 'The word under the seconds.' },
    ],
  },
  {
    type: 'dcms.social',
    version: 1,
    label: 'Social links',
    description: 'Icons linking to your profiles — Instagram, Facebook, LinkedIn and others.',
    category: 'Navigation',
    keywords: ['social media', 'instagram', 'facebook', 'follow us', 'profiles', 'icons'],
    component: SocialLinks,
    props: [
      ...SOCIAL_NETWORKS.map(
        (n) =>
          ({
            kind: 'url',
            name: n.prop,
            label: n.label,
            group: 'content',
            description: n.prop === 'email' ? 'An email address visitors can write to.' : `Your ${n.label} profile’s address. Leave empty to leave it out.`,
          }) as const,
      ),
      select('look', 'Look', SOCIAL_LOOKS, 'circles', { icons: 'Icons', circles: 'Icons in circles', labels: 'Icons with names' }, {
        group: 'style',
        description: 'How the links are drawn.',
      }),
    ],
  },
];
