import { useQuery } from '@tanstack/react-query';
import type { Editor } from 'grapesjs';
import { Blocks, FileText, GitBranch, Layers, LayoutTemplate, Puzzle, Rocket } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { cn } from '../../lib/cn';
import { DeploymentsView, SourceControlView, gitApi, useVfs } from '../site-source';
import { BlocksPanel } from './panels/BlocksPanel';
import { LayersPanel } from './panels/LayersPanel';
import { ComponentsPanel } from './panels/ComponentsPanel';
import { LayoutPanel } from './panels/LayoutPanel';
import { PagesPanel } from './panels/PagesPanel';
import { useBuilder } from './store';

export type SidebarView = 'blocks' | 'layers' | 'pages' | 'layout' | 'components' | 'scm' | 'deploy';

/**
 * The builder's left rail. Source Control and Deployments are the very same
 * components the Mode B IDE uses — a Mode A site is git-backed in exactly the
 * same way, so branches, diffs, merges and build history need no second
 * implementation.
 */
export function BuilderSidebar({
  siteId,
  view,
  onViewChange,
  editor,
}: {
  siteId: string;
  view: SidebarView;
  onViewChange: (v: SidebarView) => void;
  editor: Editor | null;
}) {
  const { t } = useTranslation();
  const branch = useVfs((s) => s.branch);
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
  });

  return (
    <div className="flex h-full">
      <div className="flex w-11 shrink-0 flex-col items-center gap-1 border-r py-2">
        <RailButton active={view === 'blocks'} label={t('builder.blocks')} onClick={() => onViewChange('blocks')}>
          <Blocks className="h-5 w-5" />
        </RailButton>
        <RailButton active={view === 'layers'} label={t('builder.layers')} onClick={() => onViewChange('layers')}>
          <Layers className="h-5 w-5" />
        </RailButton>
        <RailButton active={view === 'pages'} label={t('builder.pages')} onClick={() => onViewChange('pages')}>
          <FileText className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'layout'}
          label={t('builder.regions.title')}
          onClick={() => onViewChange('layout')}
        >
          <LayoutTemplate className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'components'}
          label={t('builder.components.title')}
          onClick={() => onViewChange('components')}
        >
          <Puzzle className="h-5 w-5" />
        </RailButton>
        <RailButton
          active={view === 'scm'}
          label={t('ide.git.title')}
          onClick={() => onViewChange('scm')}
          badge={changes.data?.length ?? 0}
        >
          <GitBranch className="h-5 w-5" />
        </RailButton>
        <RailButton active={view === 'deploy'} label={t('ide.deploy.title')} onClick={() => onViewChange('deploy')}>
          <Rocket className="h-5 w-5" />
        </RailButton>
      </div>

      <div className="min-w-0 flex-1 overflow-hidden">
        {view === 'blocks' && <BlocksPanel editor={editor} />}
        {view === 'layers' && <LayersPanel editor={editor} />}
        {view === 'pages' && <PagesPanel />}
        {view === 'layout' && <LayoutPanel />}
        {view === 'components' && <ComponentsPanel />}
        {view === 'scm' && (
          <SourceControlView
            siteId={siteId}
            branch={branch}
            onSwitchBranch={(b) => useVfs.getState().setBranch(b)}
            onReload={() => useBuilder.getState().requestReload()}
            onRestored={(files, version, hashes) => {
              useVfs.getState().load(files, version, hashes);
              useBuilder.getState().syncFromVfs();
              useBuilder.getState().requestReload();
            }}
          />
        )}
        {view === 'deploy' && <DeploymentsView siteId={siteId} />}
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
