import { ChevronRight } from 'lucide-react';
import { useTranslation } from 'react-i18next';

/**
 * Where the open file sits.
 *
 * <p>The tab strip shows `App.tsx`, and in a project holding both `src/App.tsx` and
 * `src/routes/App.tsx` that does not say which one you are editing. The full path existed only
 * in a `title` attribute, which means it existed only for people who hover.</p>
 *
 * <p><b>Path context, not navigation.</b> Clicking a folder to reveal it in the explorer is the
 * obvious next feature and is deliberately not here: the tree owns its own collapsed-folder
 * state, and lifting that out to serve a hover-target would be a large change to the tree in
 * order to duplicate what clicking the folder in the tree already does. This answers the
 * question it exists for — which file is this — and stops.</p>
 */
export function Breadcrumbs({ path }: { path: string | null }) {
  const { t } = useTranslation();

  // A fixed-height bar either way, so the editor below does not jump by 24px each time the
  // last tab closes.
  if (!path) return <div className="h-6 shrink-0 border-b bg-card" />;

  const segments = path.split('/');

  return (
    <nav
      aria-label={t('ide.breadcrumbs')}
      className="flex h-6 shrink-0 items-center gap-0.5 overflow-x-auto border-b bg-card px-3 font-mono text-[11px] text-muted-foreground"
    >
      {segments.map((segment, index) => (
        <span key={segments.slice(0, index + 1).join('/')} className="flex shrink-0 items-center gap-0.5">
          {index > 0 ? (
            <ChevronRight className="h-3 w-3 shrink-0 text-muted-foreground/50" aria-hidden />
          ) : null}
          <span className={index === segments.length - 1 ? 'text-foreground' : undefined}>
            {segment}
          </span>
        </span>
      ))}
    </nav>
  );
}
