import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import { CenteredSpinner, cn, useCan } from '@dcms/ui';
import { SETTINGS_SECTIONS, type SettingsSection } from '../../app/routeGuards';

/**
 * The Settings area: one destination in the sidebar, with its own sub-navigation.
 *
 * <p>Six top-level entries — Members, Roles, Audit log, Domains, AI, API Docs — used to sit
 * beside Content, Media and Sites, which put "what this workspace is called" at the same level
 * as the work people come here to do, and grew the menu by one line every time the product
 * gained a setting.</p>
 *
 * <p><b>No page header of its own.</b> Each section already renders one, and two `h1`s on a
 * page is a broken document outline before it is a design problem — a screen reader announces
 * two competing titles and the reader cannot tell which page they are on. The area is named by
 * the label above the sub-nav instead. For the same reason this does not wrap the outlet in
 * `Page`: each section brings its own, and nesting them would double the padding.</p>
 *
 * <p>Sections are filtered by permission, the same rule the sidebar uses: a section you cannot
 * open is <b>absent</b> rather than disabled. That is deliberately the opposite of the rule for
 * buttons — a disabled button teaches you the permission to ask for, while a list of greyed-out
 * section links teaches nothing and makes the ones you can use harder to find.</p>
 */
export function SettingsLayout() {
  const { t } = useTranslation();
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col lg:flex-row">
      <nav
        aria-label={t('nav.settings')}
        className="shrink-0 px-6 pt-6 lg:sticky lg:top-6 lg:w-60 lg:self-start lg:pr-0"
      >
        <p className="px-3 pb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('nav.settings')}
        </p>
        <ul className="space-y-0.5">
          {SETTINGS_SECTIONS.map((section) => (
            <SectionLink key={section.to} section={section} pathname={pathname} />
          ))}
        </ul>
      </nav>

      <div className="min-w-0 flex-1">
        {/*
          Its own boundary. Every section is lazily loaded, and without one here the suspension
          reaches the shell's boundary — which unmounts this layout, so moving between sections
          blanks the sub-navigation you are moving through.
        */}
        <Suspense fallback={<CenteredSpinner />}>
          <Outlet />
        </Suspense>
      </div>
    </div>
  );
}

function SectionLink({ section, pathname }: { section: SettingsSection; pathname: string }) {
  const { t } = useTranslation();
  // A hook, so it cannot be called inside the map above — hence a component per row.
  const allowed = useCan(section.perm);
  if (section.perm && !allowed) return null;

  const active = pathname === section.to;

  return (
    <li>
      <Link
        to={section.to as string}
        aria-current={active ? 'page' : undefined}
        className={cn(
          'block rounded-md px-3 py-2 text-sm transition-colors',
          active
            ? 'bg-accent font-medium text-accent-foreground'
            : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground',
        )}
      >
        <span className="block">{t(section.labelKey)}</span>
        {/*
          The description follows the row it is in. `text-muted-foreground` over the tinted
          active background measures 4.17:1 — under the 4.5:1 minimum, and axe caught it on the
          one row somebody is currently reading.
        */}
        <span
          className={cn(
            'mt-0.5 block text-xs font-normal',
            active ? 'text-accent-foreground/80' : 'text-muted-foreground',
          )}
        >
          {t(section.descriptionKey)}
        </span>
      </Link>
    </li>
  );
}
