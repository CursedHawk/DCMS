import { Link } from '@tanstack/react-router';
import { ArrowLeft, Bot } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@dcms/ui';
import { useAi } from '../assistant/context';
import { JobsIndicator } from '../jobs/JobsIndicator';
import { NotificationBell } from '../notifications/NotificationBell';

/**
 * The one bar across the top of a site editor.
 *
 * <p>Editors open in focus mode: no app sidebar, no app topbar — the editor's own rail and
 * panels are navigation enough, and the app's sidebar beside them made three stacked left
 * columns. So this bar carries what the topbar would have: the way back, which site this is,
 * and the three things that report on work in progress (the assistant, background jobs,
 * notifications). Each editor puts its own tools in `children`.</p>
 *
 * <p>It used to be four hand-built toolbars, one per editor, each with its own back arrow — only
 * one of which had an accessible name.</p>
 */
export function EditorHeader({
  name,
  mode,
  children,
}: {
  name: string | undefined;
  /** What kind of site this is — "React app", "Static files". */
  mode: string;
  children?: React.ReactNode;
}) {
  const { t } = useTranslation();
  const ai = useAi();

  return (
    <div className="flex h-12 shrink-0 items-center gap-2 border-b bg-card px-3">
      <Link
        to={'/sites' as string}
        aria-label={t('sites.backToSites')}
        title={t('sites.backToSites')}
        className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-md transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <ArrowLeft className="h-4 w-4" aria-hidden />
      </Link>
      <div className="flex min-w-0 shrink items-baseline gap-2">
        {/* The page's heading: an editor has no other, and a screen reader lands here. */}
        <h1 className="truncate text-sm font-medium" title={name}>
          {name}
        </h1>
        <span className="hidden shrink-0 text-xs text-muted-foreground sm:inline">{mode}</span>
      </div>

      {children}

      <div className="ml-auto flex shrink-0 items-center gap-1 border-l pl-2">
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('assistant.open')}
          onClick={() => ai.setOpen(true)}
        >
          <Bot className="h-4 w-4" aria-hidden />
        </Button>
        <JobsIndicator enabled />
        <NotificationBell enabled />
      </div>
    </div>
  );
}
