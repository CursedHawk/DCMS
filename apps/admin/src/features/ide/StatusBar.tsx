import { useQuery } from '@tanstack/react-query';
import { GitBranch, Pencil } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { RELEASE_BRANCH } from './constants';
import { gitApi } from './git';

// A slim VS Code-style status bar: current branch (click to open Source Control)
// and the pending-change count. The release branch is flagged so users know a
// commit there publishes.
export function StatusBar({
  siteId,
  branch,
  dirty,
  onOpenScm,
}: {
  siteId: string;
  branch: string;
  dirty: boolean;
  onOpenScm: () => void;
}) {
  const { t } = useTranslation();
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
  });
  const count = changes.data?.length ?? 0;

  return (
    <div className="flex h-6 shrink-0 items-center gap-3 border-t bg-card px-3 text-[11px] text-muted-foreground">
      <button
        type="button"
        onClick={onOpenScm}
        className="flex items-center gap-1 hover:text-foreground"
        title={t('ide.git.branch')}
      >
        <GitBranch className="h-3 w-3" />
        {branch}
        {branch === RELEASE_BRANCH && <span className="text-[hsl(var(--warning))]">({t('ide.git.live')})</span>}
      </button>
      <button type="button" onClick={onOpenScm} className="flex items-center gap-1 hover:text-foreground">
        <Pencil className="h-3 w-3" />
        {t('ide.git.changesCount', { count })}
      </button>
      {dirty && <span className="text-muted-foreground">{t('common.saving')}</span>}
    </div>
  );
}
