import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Check,
  ChevronDown,
  ChevronRight,
  Copy,
  ExternalLink,
  FilePlus2,
  FileMinus2,
  FilePen,
  GitCommitHorizontal,
  GitMerge,
  Plus,
  RotateCcw,
} from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '../../components/ui/button';
import { CenteredSpinner } from '../../components/ui/spinner';
import { ApiError } from '../../lib/api';
import { cn } from '../../lib/cn';
import type { GitChange } from './git';
import { gitApi } from './git';
import { MergeDialog } from './MergeDialog';
import { RELEASE_BRANCH } from './constants';
import { useVfs } from './vfs';

// The Source Control view that lives in the IDE's left sidebar (VS Code style):
// branch selector + create, a commit box, the working-draft changes list (clicking
// a file opens a diff tab in the editor), a "Merge into release" action, and the
// git history + clone URLs.
export function SourceControlView({
  siteId,
  branch,
  onSwitchBranch,
  onReload,
  onRestored,
}: {
  siteId: string;
  branch: string;
  onSwitchBranch: (branch: string) => void;
  onReload: () => void;
  onRestored: (files: Record<string, string>, version: number, hashes: Record<string, string>) => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const openDiff = useVfs((s) => s.openDiff);
  const [message, setMessage] = useState('');
  const [description, setDescription] = useState('');
  const [newBranchName, setNewBranchName] = useState('');
  const [showNewBranch, setShowNewBranch] = useState(false);
  const [mergeOpen, setMergeOpen] = useState(false);
  const [showHistory, setShowHistory] = useState(true);
  const [showClone, setShowClone] = useState(false);

  const status = useQuery({ queryKey: ['git-status', siteId], queryFn: () => gitApi.status(siteId) });
  const provisioned = !!status.data?.provisioned;
  const branches = useQuery({
    queryKey: ['git-branches', siteId],
    queryFn: () => gitApi.branches(siteId),
    enabled: provisioned,
  });
  const changes = useQuery({
    queryKey: ['git-changes', siteId, branch],
    queryFn: () => gitApi.changes(siteId, branch),
    enabled: provisioned,
  });
  const history = useQuery({
    queryKey: ['git-history', siteId, branch],
    queryFn: () => gitApi.history(siteId, branch),
    enabled: provisioned,
  });

  const afterCommit = () => {
    setMessage('');
    setDescription('');
    setNewBranchName('');
    setShowNewBranch(false);
    queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
    queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
    queryClient.invalidateQueries({ queryKey: ['git-branches', siteId] });
  };

  const commit = useMutation({
    mutationFn: (toNewBranch: boolean) =>
      gitApi.commit(siteId, {
        branch,
        message: message.trim(),
        description: description.trim() || undefined,
        newBranch: toNewBranch ? newBranchName.trim() : undefined,
      }),
    onSuccess: (res, toNewBranch) => {
      toast.success(t('ide.git.committed'));
      afterCommit();
      if (toNewBranch) onSwitchBranch(res.branch);
      else onReload();
    },
    onError: (e) => {
      if (e instanceof ApiError && e.status === 409) toast.error(t('ide.git.branchMoved'));
      else toast.error(t('errors.generic'));
    },
  });

  const createBranch = useMutation({
    mutationFn: (name: string) => gitApi.createBranch(siteId, name, branch),
    onSuccess: (res) => {
      toast.success(t('ide.git.branchCreated'));
      queryClient.invalidateQueries({ queryKey: ['git-branches', siteId] });
      onSwitchBranch(res.name);
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const restore = useMutation({
    mutationFn: (sha: string) => gitApi.restore(siteId, sha, branch),
    onSuccess: (res) => {
      toast.success(t('ide.restored'));
      onRestored(res.files, res.version, res.hashes);
      queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
    },
    onError: () => toast.error(t('errors.generic')),
  });

  const openChangeDiff = (c: GitChange) =>
    openDiff({ path: c.path, status: c.status, original: c.headContent, modified: c.draftContent });

  if (status.isLoading) return <CenteredSpinner label={t('common.loading')} />;
  const s = status.data;
  if (s && !s.enabled) return <p className="p-3 text-sm text-muted-foreground">{t('ide.git.disabled')}</p>;
  if (s?.enabled && !provisioned)
    return <p className="p-3 text-sm text-muted-foreground">{t('ide.git.provisioning')}</p>;

  const changeList = changes.data ?? [];
  const onRelease = branch === RELEASE_BRANCH;

  return (
    <div className="flex h-full flex-col overflow-auto">
      {/* Branch bar */}
      <div className="flex shrink-0 items-center gap-1.5 border-b p-2">
        <select
          value={branch}
          onChange={(e) => onSwitchBranch(e.target.value)}
          className="min-w-0 flex-1 rounded border bg-background px-2 py-1 text-xs outline-none focus:ring-1 focus:ring-ring"
          title={t('ide.git.branch')}
        >
          {(branches.data ?? [{ name: branch }]).map((b) => (
            <option key={b.name} value={b.name}>
              {b.name}
            </option>
          ))}
        </select>
        <Button
          variant="ghost"
          size="icon"
          title={t('ide.git.createBranch')}
          disabled={createBranch.isPending}
          onClick={() => {
            const name = window.prompt(t('ide.git.newBranchName'))?.trim();
            if (name) createBranch.mutate(name);
          }}
        >
          <Plus className="h-4 w-4" />
        </Button>
      </div>

      {/* Commit box */}
      <div className="shrink-0 space-y-2 border-b p-2">
        <input
          value={message}
          onChange={(e) => setMessage(e.target.value)}
          placeholder={t('ide.git.commitMessage')}
          className="w-full rounded border bg-background px-2 py-1.5 text-sm outline-none focus:ring-1 focus:ring-ring"
        />
        <textarea
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          placeholder={t('ide.git.commitDescription')}
          rows={2}
          className="w-full resize-y rounded border bg-background px-2 py-1.5 text-xs outline-none focus:ring-1 focus:ring-ring"
        />
        {showNewBranch && (
          <input
            value={newBranchName}
            onChange={(e) => setNewBranchName(e.target.value)}
            placeholder={t('ide.git.newBranchName')}
            className="w-full rounded border bg-background px-2 py-1.5 text-xs outline-none focus:ring-1 focus:ring-ring"
          />
        )}
        <div className="flex items-center gap-1.5">
          <Button
            className="min-w-0 flex-1"
            size="sm"
            disabled={!message.trim() || commit.isPending || (showNewBranch && !newBranchName.trim())}
            onClick={() => commit.mutate(showNewBranch)}
          >
            <GitCommitHorizontal className="h-4 w-4 shrink-0" />
            <span className="truncate">
              {showNewBranch ? t('ide.git.commitToNewBranch') : t('ide.git.commitTo', { branch })}
            </span>
          </Button>
          <Button
            variant="outline"
            size="icon"
            title={t('ide.git.newBranch')}
            onClick={() => setShowNewBranch((v) => !v)}
          >
            <Plus className="h-4 w-4" />
          </Button>
        </div>
        {!onRelease && (
          <Button variant="outline" size="sm" className="w-full" onClick={() => setMergeOpen(true)}>
            <GitMerge className="h-4 w-4" /> {t('ide.git.mergeToRelease')}
          </Button>
        )}
      </div>

      {/* Changes */}
      <div className="shrink-0 border-b">
        <div className="px-2 pt-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
          {t('ide.git.changes')} {changeList.length > 0 && `(${changeList.length})`}
        </div>
        {changes.isLoading && <div className="p-2 text-xs text-muted-foreground">{t('common.loading')}</div>}
        {!changes.isLoading && changeList.length === 0 && (
          <p className="p-2 text-xs text-muted-foreground">{t('ide.git.noChanges')}</p>
        )}
        <ul className="py-1">
          {changeList.map((c) => (
            <li key={c.path}>
              <button
                type="button"
                onClick={() => openChangeDiff(c)}
                className="flex w-full items-center gap-1.5 px-2 py-1 text-left text-xs hover:bg-accent/50"
                title={c.path}
              >
                <ChangeIcon status={c.status} />
                <span className="min-w-0 flex-1 truncate">{c.path}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>

      {/* History */}
      <div className="shrink-0 border-b">
        <SectionHeader
          label={t('ide.git.history')}
          open={showHistory}
          onToggle={() => setShowHistory((v) => !v)}
        />
        {showHistory && (
          <ul>
            {history.data?.length === 0 && (
              <p className="p-2 text-xs text-muted-foreground">{t('ide.git.noCommits')}</p>
            )}
            {history.data?.map((c) => (
              <li key={c.sha} className="flex items-start gap-1.5 px-2 py-1.5">
                <GitCommitHorizontal className="mt-0.5 h-3 w-3 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-xs">{(c.message ?? '').split('\n')[0]}</p>
                  <p className="text-[10px] text-muted-foreground">
                    <code>{c.shortSha}</code>
                    {c.author ? ` · ${c.author}` : ''}
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => restore.mutate(c.sha)}
                  disabled={restore.isPending}
                  title={t('ide.git.restore')}
                  className="mt-0.5 shrink-0 text-muted-foreground hover:text-foreground disabled:opacity-50"
                >
                  <RotateCcw className="h-3 w-3" />
                </button>
                {c.htmlUrl && (
                  <a
                    href={c.htmlUrl}
                    target="_blank"
                    rel="noreferrer"
                    title={t('ide.git.viewInForgejo')}
                    className="mt-0.5 text-muted-foreground hover:text-foreground"
                  >
                    <ExternalLink className="h-3 w-3" />
                  </a>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>

      {/* Clone */}
      <div className="shrink-0">
        <SectionHeader label={t('ide.git.clone')} open={showClone} onToggle={() => setShowClone((v) => !v)} />
        {showClone && s && (
          <div className="space-y-1.5 p-2">
            <CopyRow label="HTTPS" value={s.httpUrl ?? ''} />
            <CopyRow label="SSH" value={s.sshUrl ?? ''} />
          </div>
        )}
      </div>

      <MergeDialog
        siteId={siteId}
        head={branch}
        open={mergeOpen}
        onOpenChange={setMergeOpen}
        onMerged={() => {
          queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
          queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
        }}
      />
    </div>
  );
}

function SectionHeader({ label, open, onToggle }: { label: string; open: boolean; onToggle: () => void }) {
  return (
    <button
      type="button"
      onClick={onToggle}
      className="flex w-full items-center gap-1 px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground hover:text-foreground"
    >
      {open ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
      {label}
    </button>
  );
}

function ChangeIcon({ status }: { status: GitChange['status'] }) {
  if (status === 'added') return <FilePlus2 className="h-3.5 w-3.5 shrink-0 text-green-600" />;
  if (status === 'deleted') return <FileMinus2 className="h-3.5 w-3.5 shrink-0 text-destructive" />;
  return <FilePen className="h-3.5 w-3.5 shrink-0 text-amber-600" />;
}

function CopyRow({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard blocked — no-op */
    }
  };
  return (
    <div className="flex items-center gap-1.5">
      <span className="w-10 shrink-0 text-[10px] font-medium text-muted-foreground">{label}</span>
      <code className="min-w-0 flex-1 truncate rounded border bg-background px-1.5 py-1 text-[11px]">{value}</code>
      <button
        type="button"
        onClick={copy}
        className={cn('shrink-0 rounded p-1', copied ? 'text-green-600' : 'text-muted-foreground hover:text-foreground')}
      >
        {copied ? <Check className="h-3 w-3" /> : <Copy className="h-3 w-3" />}
      </button>
    </div>
  );
}
