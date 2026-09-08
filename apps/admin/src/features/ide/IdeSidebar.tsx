import { useQuery } from '@tanstack/react-query';
import { CircleAlert, Files, GitBranch, Rocket, Search, Sparkles } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { DeploymentsView, FileTree, SearchView, SourceControlView, gitApi } from '../site-source';
import { AgentPanel } from './agent/AgentPanel';
import { countBySeverity, type BuildProblem } from './preview/problems';
import { ProblemsView } from './ProblemsView';

export type SidebarView = 'files' | 'search' | 'scm' | 'problems' | 'agent' | 'deploy';

// VS Code-style left sidebar: a slim activity rail (Explorer / Source Control)
// plus the active view. The Source Control icon carries a badge with the number
// of pending changes on the current branch.
export function IdeSidebar({
  siteId,
  siteName,
  branch,
  view,
  onViewChange,
  onSwitchBranch,
  onReload,
  onRestored,
  viewWidth,
  problems,
  building,
  previewEnabled,
  onEnablePreview,
}: {
  siteId: string;
  siteName?: string;
  branch: string;
  view: SidebarView;
  onViewChange: (v: SidebarView) => void;
  onSwitchBranch: (branch: string) => void;
  onReload: () => void;
  onRestored: (files: Record<string, string>, version: number, hashes: Record<string, string>) => void;
  /** Width (px) of the active-view panel; the activity rail stays fixed. */
  viewWidth?: number;
  /** What the last build reported. Owned by IdePage, which drives the build. */
  problems: readonly BuildProblem[];
  building: boolean;
  previewEnabled: boolean;
  onEnablePreview: () => void;
}) {
  const { t } = useTranslation();
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
  });
  const changeCount = changes.data?.length ?? 0;
  const { errors, warnings } = countBySeverity(problems);

  return (
    <div className="flex h-full">
      {/* Activity rail */}
      <div className="flex w-11 shrink-0 flex-col items-center gap-1 border-r bg-card py-2">
        <RailButton
          active={view === 'files'}
          label={t('ide.explorer')}
          onClick={() => onViewChange('files')}
        >
          <Files className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'search'}
          label={t('ide.search.title')}
          onClick={() => onViewChange('search')}
        >
          <Search className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'scm'}
          label={t('ide.git.title')}
          onClick={() => onViewChange('scm')}
          badge={changeCount}
        >
          <GitBranch className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'problems'}
          label={t('ide.problems.title')}
          onClick={() => onViewChange('problems')}
          badge={errors + warnings}
          badgeTone={errors > 0 ? 'error' : 'warning'}
        >
          <CircleAlert className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'deploy'}
          label={t('ide.deploy.title')}
          onClick={() => onViewChange('deploy')}
        >
          <Rocket className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'agent'}
          label={t('ide.agent.title', 'Assistant')}
          onClick={() => onViewChange('agent')}
        >
          <Sparkles className="h-5 w-5" />
        </RailButton>
      </div>

      {/* Active view */}
      <div
        className="shrink-0 border-r bg-card"
        style={{ width: viewWidth != null ? `${viewWidth}px` : '15rem' }}
      >
        {view === 'files' && <FileTree />}
        {view === 'search' && <SearchView />}
        {view === 'scm' && (
          <SourceControlView
            siteId={siteId}
            branch={branch}
            onSwitchBranch={onSwitchBranch}
            onReload={onReload}
            onRestored={onRestored}
          />
        )}
        {view === 'problems' && (
          <ProblemsView
            problems={problems}
            building={building}
            previewEnabled={previewEnabled}
            onEnablePreview={onEnablePreview}
          />
        )}
        {view === 'deploy' && <DeploymentsView siteId={siteId} />}
        {view === 'agent' && <AgentPanel siteId={siteId} siteName={siteName} />}
      </div>
    </div>
  );
}

function RailButton({
  active,
  label,
  onClick,
  badge,
  badgeTone = 'default',
  children,
}: {
  active: boolean;
  label: string;
  onClick: () => void;
  badge?: number;
  /** Errors are not the same news as pending changes, and the badge should not say they are. */
  badgeTone?: 'default' | 'error' | 'warning';
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={label}
      className={cn(
        'relative flex h-10 w-10 items-center justify-center rounded-md',
        active ? 'bg-accent text-accent-foreground' : 'text-muted-foreground hover:text-foreground',
      )}
    >
      {children}
      {badge != null && badge > 0 && (
        <span
          className={cn(
            'absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full px-1 text-[10px] font-semibold',
            badgeTone === 'error' && 'bg-destructive text-destructive-foreground',
            // Amber is bright in both themes, so the count on it is the page's own dark ink
            // rather than a foreground token that flips with the theme and disappears.
            badgeTone === 'warning' && 'bg-[hsl(var(--warning))] text-neutral-950',
            badgeTone === 'default' && 'bg-primary text-primary-foreground',
          )}
        >
          {badge}
        </span>
      )}
    </button>
  );
}
