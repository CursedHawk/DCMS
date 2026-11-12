import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Files, GitBranch, History, Package, Rocket, Search, Zap } from 'lucide-react';
import { AgentRailIcon } from '../agent/AgentRailIcon';
import { useAgentFocus } from '../agent/hostStore';
import { useTranslation } from 'react-i18next';
import { cn } from '@dcms/ui';
import { DeploymentsView, FileTree, SearchView, SourceControlView, gitApi, isRepoWorkspace } from '../site-source';
import { AgentPanel } from './agent/AgentPanel';
import { FunctionsView } from './functions/FunctionsView';
import { PackagesView } from './functions/PackagesView';
import { RunsView, type Replay } from './functions/RunsView';
import { NewFunctionDialog } from './functions/NewFunctionDialog';

export type SidebarView = 'files' | 'search' | 'scm' | 'agent' | 'deploy' | 'functions' | 'packages' | 'runs';

/**
 * The left sidebar: a slim activity rail plus the active view.
 *
 * <p><b>Problems is no longer here.</b> A problem row reads `path:line  message` — it runs
 * across, and a 260px column wrapped every one of them onto three lines. Worse, it competed for
 * the same space as the file tree, so you could look at the error or at the file it was in but
 * never both. It lives in the bottom panel now, under the editor it is about.</p>
 *
 * <p>The agent stays a full-height view rather than moving to the bottom panel with the other
 * log-shaped surfaces: a conversation is tall and narrow and a bottom panel is short and
 * wide.</p>
 */
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
  functionsApi,
  runsFunction,
  onReplay,
}: {
  siteId: string;
  siteName?: string;
  branch: string;
  view: SidebarView;
  onViewChange: (v: SidebarView) => void;
  onSwitchBranch: (branch: string) => void;
  onReload: () => void;
  onRestored: (
    files: Record<string, string>,
    version: number,
    hashes: Record<string, string>,
  ) => void;
  /** Width (px) of the active-view panel; the activity rail stays fixed. */
  viewWidth?: number;
  /** A Functions app's repository: the agent works with its builds and its SDK. */
  functionsApi?: string;
  /** The Runs view's function filter when the IDE was opened on it. */
  runsFunction?: string;
  /** "Replay against the draft" in the Runs view: the page opens the Run tab with it. */
  onReplay?: (replay: Replay) => void;
}) {
  const repo = isRepoWorkspace(siteId);
  const [agentOpened, setAgentOpened] = useState(false);
  const [newFunction, setNewFunction] = useState(false);
  const onNewFunction = functionsApi ? () => setNewFunction(true) : undefined;
  if (view === 'agent' && !agentOpened) setAgentOpened(true);
  useAgentFocus(siteId, () => onViewChange('agent'));
  const { t } = useTranslation();
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
  });
  const changeCount = changes.data?.length ?? 0;

  return (
    <div className="flex h-full">
      {functionsApi && (
        <NewFunctionDialog
          open={newFunction}
          onOpenChange={setNewFunction}
          app={/\/plugins\/([^/]+)\//.exec(functionsApi)?.[1] ?? 'app'}
        />
      )}
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
        {repo ? null : (
        <RailButton
          active={view === 'deploy'}
          label={t('ide.deploy.title')}
          onClick={() => onViewChange('deploy')}
        >
          <Rocket className="h-5 w-5" />
        </RailButton>
        )}
        {functionsApi && (
          <RailButton
            active={view === 'functions'}
            label={t('ide.fn.title')}
            onClick={() => onViewChange('functions')}
          >
            <Zap className="h-5 w-5" />
          </RailButton>
        )}
        {functionsApi && (
          <RailButton
            active={view === 'packages'}
            label={t('ide.fn.packages.title')}
            onClick={() => onViewChange('packages')}
          >
            <Package className="h-5 w-5" />
          </RailButton>
        )}
        {functionsApi && (
          <RailButton
            active={view === 'runs'}
            label={t('ide.runs.title')}
            onClick={() => onViewChange('runs')}
          >
            <History className="h-5 w-5" />
          </RailButton>
        )}
        {/* The site agent's tools speak to a site; a repository has one only where its plugin brings tools. */}
        {repo && !functionsApi ? null : (
        <RailButton
          active={view === 'agent'}
          label={t('ide.agent.title')}
          onClick={() => onViewChange('agent')}
        >
          <AgentRailIcon siteId={siteId} />
        </RailButton>
        )}
      </div>

      {/* Active view */}
      <div
        className="shrink-0 border-r bg-card"
        style={{ width: viewWidth != null ? `${viewWidth}px` : '15rem' }}
      >
        {view === 'files' && <FileTree onNewFunction={onNewFunction} />}
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
        {view === 'deploy' && <DeploymentsView siteId={siteId} />}
        {view === 'functions' && functionsApi && <FunctionsView functionsApi={functionsApi} onNew={onNewFunction} />}
        {view === 'packages' && functionsApi && <PackagesView functionsApi={functionsApi} />}
        {view === 'runs' && functionsApi && (
          <RunsView functionsApi={functionsApi} initialFunction={runsFunction} onReplay={(r) => onReplay?.(r)} />
        )}
        {/* Kept mounted once opened, so the conversation stays on screen across views. The run
            itself lives in the agent host and outlives the page (ADR 0033). */}
        {agentOpened && (
          <div className={view === 'agent' ? 'h-full' : 'hidden'}>
            {functionsApi ? (
              <AgentPanel key={siteId} siteId={siteId} siteName={siteName} kind="functions" functionsApi={functionsApi} />
            ) : (
              <AgentPanel key={siteId} siteId={siteId} siteName={siteName} />
            )}
          </div>
        )}
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
            'absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full px-1 text-2xs font-semibold',
            badgeTone === 'error' && 'bg-destructive text-destructive-foreground',
            // Amber is bright in both themes, so the count on it is the page's own dark ink
            // rather than a foreground token that flips with the theme and disappears.
            badgeTone === 'warning' && 'bg-warning text-neutral-950',
            badgeTone === 'default' && 'bg-primary text-primary-foreground',
          )}
        >
          {badge}
        </span>
      )}
    </button>
  );
}
