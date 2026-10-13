import { Link } from '@tanstack/react-router';
import { ChevronRight } from 'lucide-react';
import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';

export interface Crumb {
  label: string;
  /** An absolute path. Omitted on the last crumb, which is the page you are on. */
  to?: string;
}

/**
 * Where a page sits, for pages more than one step from the menu: a plugin instance, a screen
 * of one, a plugin's reference.
 *
 * <p>Not on top-level pages — the menu already says where they are — and not on the full-screen
 * editors, which keep their own back button. The last crumb is the current page: text, not a
 * link to itself, and marked as current for a screen reader.</p>
 */
export function Breadcrumbs({ items }: { items: readonly Crumb[] }) {
  const { t } = useTranslation();
  return (
    <nav aria-label={t('nav.breadcrumbs')} className="mb-3 text-sm text-muted-foreground">
      <ol className="flex flex-wrap items-center gap-1">
        {items.map((crumb, i) => {
          const last = i === items.length - 1;
          return (
            <Fragment key={`${i}:${crumb.label}`}>
              {i > 0 ? (
                <li aria-hidden className="flex">
                  <ChevronRight className="h-3.5 w-3.5" />
                </li>
              ) : null}
              <li className="min-w-0 [overflow-wrap:anywhere]">
                {last || !crumb.to ? (
                  <span aria-current={last ? 'page' : undefined} className={cn(last && 'text-foreground')}>
                    {crumb.label}
                  </span>
                ) : (
                  <Link to={crumb.to as string} className="underline-offset-4 hover:text-foreground hover:underline">
                    {crumb.label}
                  </Link>
                )}
              </li>
            </Fragment>
          );
        })}
      </ol>
    </nav>
  );
}
