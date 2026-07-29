import { useQuery } from '@tanstack/react-query';
import { Files, GitBranch } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '../../lib/cn';
import { FileTree } from './FileTree';
import { gitApi } from './git';
import { SourceControlView } from './SourceControlView';

export type SidebarView = 'files' | 'scm';

// VS Code-style left sidebar: a slim activity rail (Explorer / Source Control)
// plus the active view. The Source Control icon carries a badge with the number
// of pending changes on the current branch.
export function IdeSidebar({
  siteId,
  branch,
  view,
  onViewChange,
  onSwitchBranch,
  onReload,
  onRestored,
  viewWidth,
}: {
  siteId: string;
  branch: string;
  view: SidebarView;
  onViewChange: (v: SidebarView) => void;
  onSwitchBranch: (branch: string) => void;
  onReload: () => void;
  onRestored: (files: Record<string, string>, version: number, hashes: Record<string, string>) => void;
  /** Width (px) of the active-view panel; the activity rail stays fixed. */
  viewWidth?: number;
}) {
  const { t } = useTranslation();
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
  });
  const changeCount = changes.data?.length ?? 0;

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
          active={view === 'scm'}
          label={t('ide.git.title')}
          onClick={() => onViewChange('scm')}
          badge={changeCount}
        >
          <GitBranch className="h-5 w-5" />
        </RailButton>
      </div>

      {/* Active view */}
      <div
        className="shrink-0 border-r bg-card"
        style={{ width: viewWidth != null ? `${viewWidth}px` : '15rem' }}
      >
        {view === 'files' ? (
          <FileTree />
        ) : (
          <SourceControlView
            siteId={siteId}
            branch={branch}
            onSwitchBranch={onSwitchBranch}
            onReload={onReload}
            onRestored={onRestored}
          />
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
  children,
}: {
  active: boolean;
  label: string;
  onClick: () => void;
  badge?: number;
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
        <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[10px] font-semibold text-primary-foreground">
          {badge}
        </span>
      )}
    </button>
  );
}
