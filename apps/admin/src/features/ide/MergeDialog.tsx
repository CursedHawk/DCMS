import { useMutation, useQuery } from '@tanstack/react-query';
import { Eye, GitMerge } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../components/ui/button';
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../../components/ui/dialog';
import { cn } from '../../lib/cn';
import { RELEASE_BRANCH } from './constants';
import type { MergeConflictFile } from './git';
import { gitApi } from './git';
import { useVfs } from './vfs';

type Side = 'branch' | 'release';

// Merge `head` into `base` (default `release` → build + deploy). Shows a pre-merge
// review (what the merge brings), performs a real 3-way merge, and — when git reports
// a true conflict — lets the user view each conflicting file's two versions and pick
// which to keep before committing the merged result.
export function MergeDialog({
  siteId,
  head,
  base = RELEASE_BRANCH,
  open,
  onOpenChange,
  onMerged,
}: {
  siteId: string;
  head: string;
  base?: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onMerged: () => void;
}) {
  const { t } = useTranslation();
  const openDiff = useVfs((s) => s.openDiff);
  const [conflicts, setConflicts] = useState<MergeConflictFile[] | null>(null);
  const [choices, setChoices] = useState<Record<string, Side>>({});

  const compare = useQuery({
    queryKey: ['git-compare', siteId, head, base],
    queryFn: () => gitApi.compare(siteId, head, base),
    enabled: open && !conflicts,
  });

  const reset = () => {
    setConflicts(null);
    setChoices({});
  };

  const done = (built: boolean) => {
    toast.success(built ? t('ide.git.mergedDeploying') : t('ide.git.upToDate'));
    reset();
    onMerged();
    onOpenChange(false);
  };

  const merge = useMutation({
    mutationFn: () => gitApi.merge(siteId, head, base),
    onSuccess: (res) => {
      if (res.conflict && res.files) {
        setConflicts(res.files);
        // Default every conflict to the incoming branch's version.
        setChoices(Object.fromEntries(res.files.map((f) => [f.path, 'branch' as Side])));
      } else {
        done(!res.upToDate);
      }
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const resolve = useMutation({
    mutationFn: () => {
      const resolutions: Record<string, string | null> = {};
      for (const f of conflicts ?? []) {
        resolutions[f.path] = choices[f.path] === 'release' ? f.releaseContent : f.branchContent;
      }
      return gitApi.resolveMerge(siteId, head, resolutions, base);
    },
    onSuccess: () => done(true),
    onError: () => toast.error(t('errors.generic')),
  });

  // Open the two conflicting versions side-by-side in the editor: base/target ("ours")
  // vs the incoming branch ("theirs"), so the user can see what differs before choosing.
  const viewConflict = (f: MergeConflictFile) =>
    openDiff({ path: f.path, status: 'modified', original: f.releaseContent, modified: f.branchContent });

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o) reset();
        onOpenChange(o);
      }}
    >
      <DialogContent wide>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <GitMerge className="h-4 w-4" />
            {t('ide.git.mergeTitle', { head, base })}
          </DialogTitle>
        </DialogHeader>

        {!conflicts ? (
          <DialogBody className="space-y-3">
            {compare.isLoading && <p className="text-sm text-muted-foreground">{t('common.loading')}</p>}
            {compare.data && (
              <>
                <p className="text-sm text-muted-foreground">
                  {t('ide.git.mergeSummary', {
                    commits: compare.data.totalCommits,
                    files: compare.data.files.length,
                  })}
                </p>
                <ul className="max-h-64 overflow-auto rounded border">
                  {compare.data.files.map((f) => (
                    <li key={f.path} className="flex items-center gap-2 border-b px-2 py-1 text-xs last:border-b-0">
                      <span className="w-16 shrink-0 text-muted-foreground">{f.status}</span>
                      <span className="min-w-0 flex-1 truncate">{f.path}</span>
                    </li>
                  ))}
                  {compare.data.files.length === 0 && (
                    <li className="px-2 py-2 text-xs text-muted-foreground">{t('ide.git.noChanges')}</li>
                  )}
                </ul>
              </>
            )}
          </DialogBody>
        ) : (
          <DialogBody className="space-y-3">
            <p className="text-sm text-destructive">{t('ide.git.conflictIntro', { count: conflicts.length })}</p>
            <ul className="space-y-2">
              {conflicts.map((f) => (
                <li key={f.path} className="rounded border p-2">
                  <div className="mb-1.5 flex items-center gap-1.5">
                    <span className="min-w-0 flex-1 truncate text-xs font-medium" title={f.path}>
                      {f.path}
                    </span>
                    <button
                      type="button"
                      onClick={() => viewConflict(f)}
                      title={t('ide.git.viewDiff')}
                      className="shrink-0 rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
                    >
                      <Eye className="h-3.5 w-3.5" />
                    </button>
                  </div>
                  <div className="flex gap-1.5">
                    <SideButton
                      active={choices[f.path] !== 'release'}
                      onClick={() => setChoices((c) => ({ ...c, [f.path]: 'branch' }))}
                    >
                      {t('ide.git.useBranch', { branch: head })}
                    </SideButton>
                    <SideButton
                      active={choices[f.path] === 'release'}
                      onClick={() => setChoices((c) => ({ ...c, [f.path]: 'release' }))}
                    >
                      {t('ide.git.useBranch', { branch: base })}
                    </SideButton>
                  </div>
                </li>
              ))}
            </ul>
          </DialogBody>
        )}

        <DialogFooter>
          {!conflicts ? (
            <Button onClick={() => merge.mutate()} disabled={merge.isPending || compare.isLoading}>
              <GitMerge className="h-4 w-4" /> {t('ide.git.merge')}
            </Button>
          ) : (
            <Button onClick={() => resolve.mutate()} disabled={resolve.isPending}>
              <GitMerge className="h-4 w-4" /> {t('ide.git.completeMerge')}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SideButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'flex-1 rounded border px-2 py-1 text-xs',
        active ? 'border-primary bg-primary/10 text-primary' : 'text-muted-foreground hover:bg-accent/50',
      )}
    >
      {children}
    </button>
  );
}
