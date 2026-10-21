import {
  BarChart3,
  Clapperboard,
  Download,
  Facebook,
  GalleryHorizontal,
  Instagram,
  Lock,
  MonitorPlay,
  Newspaper,
  Palette,
  Play,
  Search,
  ShieldCheck,
  Table2,
  TrendingUp,
  Users,
  Youtube,
} from 'lucide-react';
import type { CSSProperties, ReactNode } from 'react';

/* ---- The little pictures. Decorative: each tile's text says what the plugin does. ---- */

const photo = (a: string, b: string) => ({ background: `linear-gradient(135deg, ${a}, ${b})` });

function GalleryArt() {
  return (
    <div className="grid h-full grid-cols-4 grid-rows-2 gap-2">
      <div className="col-span-2 row-span-2 rounded-lg" style={photo('#fbbf24', '#e11d48')} />
      <div className="rounded-lg" style={photo('#38bdf8', '#1e3a8a')} />
      <div className="rounded-lg" style={photo('#34d399', '#065f46')} />
      <div className="rounded-lg" style={photo('#c084fc', '#4c1d95')} />
      <div className="rounded-lg" style={photo('#fde68a', '#d97706')} />
    </div>
  );
}

function ChatArt() {
  return (
    <div className="flex h-full flex-col justify-end gap-2 text-[0.8rem] leading-snug">
      <p className="w-fit max-w-[80%] rounded-2xl rounded-bl-md bg-background px-3 py-2 shadow-sm">
        Do you open on Sundays?
      </p>
      <p className="ml-auto w-fit max-w-[85%] rounded-2xl rounded-br-md bg-[hsl(var(--hue)_70%_50%)] px-3 py-2 text-white">
        Yes, from 10 to 4. Want me to book you a table?
      </p>
      <span className="flex w-fit gap-1 rounded-full bg-background px-3 py-2.5 shadow-sm">
        {[0, 1, 2].map((i) => (
          <span
            key={i}
            className="typing-dot size-1.5 rounded-full bg-foreground/40"
            style={{ animationDelay: `${i * 0.2}s` }}
          />
        ))}
      </span>
    </div>
  );
}

function EventsArt() {
  const rows = [
    ['24', 'Oct', 'Autumn concert', 'Main hall'],
    ['02', 'Nov', 'Open studio day', 'Workshop'],
    ['15', 'Nov', 'Winter market', 'Square'],
  ];
  return (
    <ul className="space-y-2">
      {rows.map(([d, m, t, v]) => (
        <li key={t} className="flex items-center gap-3 rounded-lg bg-background p-2 shadow-sm">
          <span className="grid w-10 shrink-0 place-items-center rounded-md bg-[hsl(var(--hue)_70%_50%)] py-1 leading-none text-white">
            <span className="text-sm font-semibold">{d}</span>
            <span className="text-[0.65rem]">{m}</span>
          </span>
          <span className="min-w-0 text-[0.8rem] leading-tight">
            <span className="block truncate font-medium">{t}</span>
            <span className="text-muted-foreground">{v}</span>
          </span>
        </li>
      ))}
    </ul>
  );
}

const wave = [3, 6, 9, 5, 11, 14, 8, 12, 16, 10, 7, 13, 9, 5, 8, 12, 6, 4, 9, 7, 11, 5, 3, 6];

function AudioArt() {
  return (
    <div className="flex h-full flex-col justify-center gap-4 rounded-lg bg-background p-3 shadow-sm">
      <div className="flex items-center gap-3">
        <span className="size-11 shrink-0 rounded-md" style={photo('#f472b6', '#7c3aed')} />
        <span className="min-w-0 text-[0.8rem] leading-tight">
          <span className="block font-medium">Late night session</span>
          <span className="text-muted-foreground">The House Band</span>
        </span>
      </div>
      <div className="flex items-center gap-3">
        <span className="grid size-8 shrink-0 place-items-center rounded-full bg-[hsl(var(--hue)_70%_50%)] text-white">
          <Play className="size-3.5 translate-x-px fill-current" />
        </span>
        <span className="flex h-6 flex-1 items-center gap-[3px]">
          {wave.map((h, i) => (
            <span
              key={i}
              className={`flex-1 rounded-full ${i < 9 ? 'bg-[hsl(var(--hue)_70%_50%)]' : 'bg-foreground/15'}`}
              style={{ height: `${25 + h * 4.5}%` }}
            />
          ))}
        </span>
      </div>
    </div>
  );
}

function FormsArt() {
  return (
    <div className="space-y-2 rounded-lg bg-background p-3 text-[0.75rem] shadow-sm">
      <div className="rounded-md border px-2.5 py-1.5 text-muted-foreground">jana@example.com</div>
      <div className="h-12 rounded-md border px-2.5 py-1.5 text-muted-foreground">
        I would like to ask about…
      </div>
      <div className="w-fit rounded-md bg-[hsl(var(--hue)_70%_45%)] px-3 py-1.5 font-medium text-white">
        Send message
      </div>
    </div>
  );
}

function BlogArt() {
  return (
    <div className="grid h-full grid-cols-2 gap-2">
      {[photo('#5eead4', '#0f766e'), photo('#fca5a5', '#b91c1c')].map((bg, i) => (
        <div key={i} className="flex flex-col rounded-lg bg-background p-2 shadow-sm">
          <span className="h-14 rounded-md" style={bg} />
          <span className="mt-2 h-2 w-4/5 rounded-full bg-foreground/70" />
          <span className="mt-1.5 h-2 w-3/5 rounded-full bg-foreground/70" />
          <span className="mt-2 h-1.5 w-2/5 rounded-full bg-foreground/20" />
        </div>
      ))}
    </div>
  );
}

function SearchArt() {
  const results = [
    ['Spring ', 'concert', ' programme'],
    ['Tickets for the ', 'concert', ' series'],
    ['Photos from last year’s ', 'concert', ''],
  ];
  return (
    <div className="space-y-2 text-[0.8rem]">
      <div className="flex items-center gap-2 rounded-full bg-background px-3 py-2 shadow-sm">
        <Search className="size-3.5 text-muted-foreground" />
        concert
      </div>
      <ul className="space-y-1 rounded-lg bg-background p-2 shadow-sm">
        {results.map(([a, b, c]) => (
          <li key={a} className="truncate rounded px-1.5 py-1">
            {a}
            <mark className="rounded-sm bg-[hsl(var(--hue)_90%_75%/0.6)] px-0.5 text-inherit">
              {b}
            </mark>
            {c}
          </li>
        ))}
      </ul>
    </div>
  );
}

function MembersArt() {
  return (
    <div className="mx-auto w-full max-w-[15rem] rounded-lg bg-background p-3 text-[0.75rem] shadow-sm">
      <div className="flex items-center gap-2 font-medium">
        <Lock className="size-3.5 text-[hsl(var(--hue)_70%_45%)]" />
        Members only
      </div>
      <div className="mt-2.5 rounded-md border px-2.5 py-1.5 text-muted-foreground">Email</div>
      <div className="mt-1.5 rounded-md border px-2.5 py-1.5 text-muted-foreground">Password</div>
      <div className="mt-2.5 rounded-md bg-foreground px-3 py-1.5 text-center font-medium text-background">
        Sign in
      </div>
    </div>
  );
}

const showcase: { name: string; body: string; hue: number; span: string; art: ReactNode }[] = [
  {
    name: 'Image Gallery',
    body: 'Photo galleries that load fast on any screen, with your originals kept safe.',
    hue: 25,
    span: 'lg:col-span-4',
    art: <GalleryArt />,
  },
  {
    name: 'AI Chatbot',
    body: 'Answers visitors from your own pages and hands the conversation to your team when needed.',
    hue: 243,
    span: 'lg:col-span-2',
    art: <ChatArt />,
  },
  {
    name: 'Events',
    body: 'Dates, venues and line-ups, listed on any page.',
    hue: 350,
    span: 'lg:col-span-2',
    art: <EventsArt />,
  },
  {
    name: 'Audio Library',
    body: 'Tracks with artwork, ready to play on any device.',
    hue: 280,
    span: 'lg:col-span-2',
    art: <AudioArt />,
  },
  {
    name: 'Forms',
    body: 'Contact and signup forms, with every submission in an inbox.',
    hue: 160,
    span: 'lg:col-span-2',
    art: <FormsArt />,
  },
  {
    name: 'Blog',
    body: 'A dated blog with categories and an archive.',
    hue: 190,
    span: 'lg:col-span-2',
    art: <BlogArt />,
  },
  {
    name: 'Sitewide Search',
    body: 'Visitors find anything you have published.',
    hue: 45,
    span: 'lg:col-span-2',
    art: <SearchArt />,
  },
  {
    name: 'Visitor Accounts',
    body: 'Sign-up, profiles and members-only pages for your visitors.',
    hue: 210,
    span: 'lg:col-span-2',
    art: <MembersArt />,
  },
];

const more = [
  {
    icon: Table2,
    name: 'Dynamic Apps',
    body: 'Your own tables, forms and automations, with an API.',
  },
  {
    icon: Newspaper,
    name: 'Articles',
    body: 'Long-form articles with authors, tags and scheduled publishing.',
  },
  { icon: Users, name: 'Roster', body: 'People and line-ups to show on any page.' },
  {
    icon: ShieldCheck,
    name: 'Staff sign-in',
    body: 'Groups and roles that decide who sees which pages and apps.',
  },
  { icon: TrendingUp, name: 'Analytics', body: 'Visitors, page views and campaign results.' },
  {
    icon: BarChart3,
    name: 'Google Analytics',
    body: 'GA4 for each site, behind the consent banner.',
  },
  { icon: Instagram, name: 'Instagram', body: 'Your Instagram feed on your site.' },
  { icon: Facebook, name: 'Facebook', body: 'Your Facebook page’s posts on your site.' },
  { icon: Youtube, name: 'YouTube', body: 'Your channel’s latest videos, ready to play on your site.' },
  {
    icon: Download,
    name: 'File Downloads',
    body: 'Files to download, with counts and access rules.',
  },
  {
    icon: Clapperboard,
    name: 'Video Gallery',
    body: 'Video collections with posters and captions.',
  },
  { icon: MonitorPlay, name: 'Video Streaming', body: 'Smooth playback for long videos.' },
  { icon: GalleryHorizontal, name: 'Carousel', body: 'Rotating banners for the top of a page.' },
  { icon: Palette, name: 'Branding', body: 'Your logo, colours and fonts across the site.' },
];

export function Plugins() {
  return (
    <>
      <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-6">
        {showcase.map((p) => (
          <li
            key={p.name}
            style={{ '--hue': p.hue } as CSSProperties}
            className={`reveal flex flex-col overflow-hidden rounded-2xl border bg-card ${p.span}`}
          >
            <div className="tint h-56 p-5" aria-hidden="true">
              {p.art}
            </div>
            <div className="p-5">
              <h3 className="font-semibold">{p.name}</h3>
              <p className="mt-1 text-[0.9375rem] text-muted-foreground">{p.body}</p>
            </div>
          </li>
        ))}
      </ul>
      <h3 className="mt-20 text-xl font-semibold">And the rest of the shelf</h3>
      <ul className="mt-6 grid gap-x-10 gap-y-6 sm:grid-cols-2 lg:grid-cols-3">
        {more.map(({ icon: Icon, name, body }) => (
          <li key={name} className="flex gap-4">
            <span className="grid size-10 shrink-0 place-items-center rounded-xl bg-accent text-accent-foreground">
              <Icon className="size-5" aria-hidden="true" />
            </span>
            <span>
              <span className="block font-medium">{name}</span>
              <span className="text-[0.9375rem] text-muted-foreground">{body}</span>
            </span>
          </li>
        ))}
      </ul>
    </>
  );
}
