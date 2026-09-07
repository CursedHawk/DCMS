import { useMutation, useQuery } from '@tanstack/react-query';
import { GitMerge } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@dcms/ui';
import { RELEASE_BRANCH } from './constants';
import { ConflictResolver } from './ConflictResolver';
import { isFullyResolved, resolvedContent, type ConflictFile, type Resolution } from './conflict';
import type { MergeConflictFile } from './git';
import { gitApi } from './git';

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
  const [conflicts, setConflicts] = useState<ConflictFile[] | null>(null);
  const [choices, setChoices] = useState<Record<string, Resolution>>({});

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
        setConflicts(res.files.map(toConflictFile));
        /*
         * Deliberately no default.
         *
         * Every conflict used to arrive pre-set to the incoming branch, which made "complete
         * merge" clickable before the reader had looked at anything — and quietly discarded the
         * target branch's side of every file they did not open. An unanswered conflict now
         * keeps the button disabled until somebody has actually decided.
         */
        setChoices({});
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
        const choice = choices[f.path];
        if (choice) resolutions[f.path] = resolvedContent(f, choice);
      }
      return gitApi.resolveMerge(siteId, head, resolutions, base);
    },
    onSuccess: () => done(true),
    onError: () => toast.error(t('errors.generic')),
  });

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
          <DialogBody>
            <ConflictResolver
              files={conflicts}
              resolutions={choices}
              onChange={setChoices}
              mineLabel={head}
              theirsLabel={base}
            />
          </DialogBody>
        )}

        <DialogFooter>
          {!conflicts ? (
            <Button onClick={() => merge.mutate()} disabled={merge.isPending || compare.isLoading}>
              <GitMerge className="h-4 w-4" /> {t('ide.git.merge')}
            </Button>
          ) : (
            <Button
              onClick={() => resolve.mutate()}
              disabled={resolve.isPending || !isFullyResolved(conflicts, choices)}
            >
              <GitMerge className="h-4 w-4" /> {t('ide.git.completeMerge')}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The merge API names its sides after the branches (`branchContent` / `releaseContent`); the
 * resolver names them after the reader's position in the merge. Same two strings, and the
 * translation belongs in one place rather than at every call site.
 */
function toConflictFile(f: MergeConflictFile): ConflictFile {
  return { path: f.path, mine: f.branchContent, theirs: f.releaseContent, base: f.baseContent };
}
