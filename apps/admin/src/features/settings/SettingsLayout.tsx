import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { Suspense } from 'react';
import { useTranslation } from 'react-i18next';
import {
  CenteredSpinner,
  SubNav,
  SubNavGroup,
  SubNavItemBody,
  subNavItemClass,
  usePermissions,
} from '@dcms/ui';
import { can } from '../../lib/permissions';
import { SETTINGS_SECTIONS } from '../../app/routeGuards';

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
  const me = usePermissions();
  const sections = SETTINGS_SECTIONS.filter((section) => !section.perm || can(me, section.perm));

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-2 px-4 pt-4 sm:px-6 sm:pt-6 lg:flex-row lg:gap-0 lg:pr-0">
      <SubNav
        label={t('nav.settings')}
        heading={t('nav.settings')}
        className="lg:sticky lg:top-6 lg:self-start"
      >
        <SubNavGroup>
          {sections.map((section) => {
            const active = pathname === section.to;
            return (
              <li key={section.to}>
                <Link
                  to={section.to as string}
                  aria-current={active ? 'page' : undefined}
                  className={subNavItemClass(active)}
                >
                  <SubNavItemBody
                    label={t(section.labelKey)}
                    description={t(section.descriptionKey)}
                    active={active}
                  />
                </Link>
              </li>
            );
          })}
        </SubNavGroup>
      </SubNav>

      {/* Each section brings its own Page, whose padding sits beside this column's. */}
      <div className="-mx-4 min-w-0 flex-1 sm:-mx-6 lg:mx-0 lg:-mt-6">
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
