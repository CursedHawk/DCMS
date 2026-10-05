import { useInRouterContext, useLocation } from 'react-router';
import { useItem } from './data';
import { text, type Props } from './kit';
import type { ComponentDefinition, ComponentRenderProps } from './registry';
import { useRenderMode } from './renderMode';
import { SiteLink, useSite } from './site';

/**
 * Navigation primitives (Mode D v2, U1.5): footer and breadcrumbs. The menu itself is
 * `dcms.nav`, which folds behind a menu button on phones.
 */

// ---------------------------------------------------------------------------
// Footer
// ---------------------------------------------------------------------------

/** The footer's small print, with `{year}` kept current. */
export function footerNote(note: string, now = new Date()): string {
  return note.replace(/\{year\}/g, String(now.getFullYear()));
}

function Footer({ props, slot }: ComponentRenderProps<Props>) {
  const note = footerNote(text(props.note, '© {year}'));
  return (
    <footer className="dcms-footer">
      <div className="dcms-width dcms-width-normal dcms-footer-inner">
        <div className="dcms-footer-top">
          {slot('brand', { className: 'dcms-footer-brand' })}
          {slot('columns', { className: 'dcms-footer-columns' })}
        </div>
        <div className="dcms-footer-bottom">
          {note && <p className="dcms-footer-note">{note}</p>}
          {slot('bottom', { className: 'dcms-footer-extra' })}
        </div>
      </div>
    </footer>
  );
}

// ---------------------------------------------------------------------------
// Breadcrumbs
// ---------------------------------------------------------------------------

interface Crumb {
  label: string;
  to?: string;
}

function routeOf(path: string, routes: readonly { path: string }[]): string | undefined {
  // An exact route, or a detail route whose :param matches this segment.
  return routes.find((r) => r.path === path)?.path ?? routes.find((r) => r.path.split('/').length === path.split('/').length && new RegExp(`^${r.path.replace(/:[^/]+/g, '[^/]+')}$`).test(path))?.path;
}

/**
 * The trail to `path`: home, then each level that is a page of the site, labelled with the
 * menu's label for it or the page's title. The last crumb is where the visitor is.
 */
export function crumbsFor(
  path: string,
  app: { routes: readonly { path: string }[]; navigation?: Readonly<Record<string, readonly { label: string; to: string }[]>> },
  titles: Readonly<Record<string, string>>,
  here?: string,
  homeLabel = 'Home',
): Crumb[] {
  const navLabels = new Map(Object.values(app.navigation ?? {}).flat().map((i) => [i.to, i.label]));
  const label = (p: string) => navLabels.get(p) ?? titles[routeOf(p, app.routes) ?? ''] ?? decodeURIComponent(p.split('/').pop() ?? '');
  const segments = path.split('/').filter(Boolean);
  const out: Crumb[] = [{ label: homeLabel, to: '/' }];
  segments.forEach((_, i) => {
    const p = `/${segments.slice(0, i + 1).join('/')}`;
    const last = i === segments.length - 1;
    if (!last && !routeOf(p, app.routes)) return;
    out.push({ label: last && here ? here : label(p), to: last ? undefined : p });
  });
  if (segments.length === 0) out[0] = { label: homeLabel };
  return out;
}

function Breadcrumbs(props: ComponentRenderProps<Props>) {
  return useInRouterContext() ? <RoutedBreadcrumbs {...props} /> : <BreadcrumbsView {...props} path={null} />;
}

function RoutedBreadcrumbs(props: ComponentRenderProps<Props>) {
  return <BreadcrumbsView {...props} path={useLocation().pathname} />;
}

function BreadcrumbsView({ props, path }: ComponentRenderProps<Props> & { path: string | null }) {
  const mode = useRenderMode();
  const { app, titles } = useSite();
  const item = useItem();
  const home = text(props.homeLabel, 'Home');
  // On the canvas there is no address: show the shape of a trail.
  const crumbs: Crumb[] =
    path === null || mode === 'edit' || !app
      ? [{ label: home, to: '/' }, { label: 'Section', to: '/' }, { label: 'This page' }]
      : crumbsFor(path, app, titles ?? {}, typeof item?.item.data.title === 'string' ? item.item.data.title : undefined, home);
  return (
    <nav className="dcms-breadcrumbs" aria-label={text(props.label, 'Breadcrumb')}>
      <ol>
        {crumbs.map((c, i) => (
          <li key={i}>
            {c.to ? (
              <SiteLink to={c.to}>{c.label}</SiteLink>
            ) : (
              <span aria-current="page">{c.label}</span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}

// ---------------------------------------------------------------------------

export const NAV_COMPONENTS: readonly ComponentDefinition[] = [
  {
    type: 'dcms.footer',
    version: 1,
    label: 'Footer',
    description: 'The bottom of every page: your name, the menus, and the small print. Put it in the app shell so every page shares it.',
    category: 'Navigation',
    keywords: ['bottom', 'copyright', 'site footer', 'contact', 'links'],
    component: Footer,
    props: [
      {
        kind: 'text',
        name: 'note',
        label: 'Small print',
        default: '© {year} Your company',
        maxLength: 200,
        group: 'content',
        description: 'The line at the very bottom. {year} becomes the current year.',
      },
    ],
    slots: [
      { name: 'brand', label: 'About' },
      { name: 'columns', label: 'Columns' },
      { name: 'bottom', label: 'Bottom bar' },
    ],
    starter: {
      brand: [
        { type: 'dcms.heading', props: { text: 'Your company', level: '4' } },
        { type: 'dcms.text', props: { text: 'One sentence about what you do.', tone: 'muted', size: 'sm' } },
      ],
      columns: [{ type: 'dcms.nav', props: { menu: 'main', layout: 'vertical' } }],
      bottom: [{ type: 'dcms.social', props: { look: 'icons', instagram: 'https://instagram.com/', facebook: 'https://facebook.com/' } }],
    },
  },
  {
    type: 'dcms.breadcrumbs',
    version: 1,
    label: 'Breadcrumbs',
    description: 'A trail like “Home › Events › Summer Jam” showing where a page sits, each step a link back.',
    category: 'Navigation',
    keywords: ['trail', 'path', 'where am i', 'navigation'],
    component: Breadcrumbs,
    props: [
      { kind: 'text', name: 'homeLabel', label: 'First step', default: 'Home', maxLength: 40, group: 'content', description: 'What the link to the home page says.' },
      { kind: 'text', name: 'label', label: 'Name (for screen readers)', default: 'Breadcrumb', maxLength: 60, group: 'content', description: 'What screen readers call the trail.' },
    ],
  },
];
