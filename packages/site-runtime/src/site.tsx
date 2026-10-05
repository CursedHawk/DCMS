import { createContext, useContext, type AnchorHTMLAttributes, type ReactNode } from 'react';
import { Link, useInRouterContext } from 'react-router';
import type { App } from './document';
import { isInternalPath, isSafeExternalHref } from './ids';
import { useRenderMode } from './renderMode';

/**
 * What a component may know about the site it is part of: the app document, for menus and
 * links. Provided by `DcmsApp` on the site and by the canvas in the builder — every node on the
 * canvas has its own React root, so the canvas provides it per node.
 */
export interface SiteInfo {
  app: App | null;
  /** Each route's page title, by path — for breadcrumbs. Absent on the canvas. */
  titles?: Readonly<Record<string, string>>;
}

export const SiteContext = createContext<SiteInfo>({ app: null });

export function useSite(): SiteInfo {
  return useContext(SiteContext);
}

type LinkProps = Omit<AnchorHTMLAttributes<HTMLAnchorElement>, 'href'> & { to: string; children?: ReactNode };

/**
 * Every link a component draws.
 *
 * - On the canvas it has no `href`: a click there selects, and a real link would navigate the
 *   editor's own frame away from the page being edited.
 * - Inside the site's router an internal path navigates client-side, without a reload.
 * - An external link must pass the protocol allow-list; one that does not renders as text.
 */
export function SiteLink({ to, children, ...rest }: LinkProps) {
  const mode = useRenderMode();
  const inRouter = useInRouterContext();
  if (mode === 'edit') return <a {...rest}>{children}</a>;
  if (isInternalPath(to)) {
    return inRouter ? (
      <Link to={to} {...rest}>
        {children}
      </Link>
    ) : (
      <a href={to} {...rest}>
        {children}
      </a>
    );
  }
  if (!isSafeExternalHref(to)) return <span className={rest.className}>{children}</span>;
  return (
    <a href={to} {...rest}>
      {children}
    </a>
  );
}
