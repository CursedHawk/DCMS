import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Check,
  ChevronDown,
  ChevronRight,
  Copy,
  ExternalLink,
  Eye,
  FilePlus2,
  FileMinus2,
  FilePen,
  GitBranch,
  GitCommitHorizontal,
  GitMerge,
  Plus,
  RotateCcw,
  X,
} from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  CenteredSpinner,
  cn,
  Dialog,
  DialogBody,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@dcms/admin-ui';
import { ApiError } from '../../lib/api';
import { can, Perm, repoRead, repoWrite, useMyPermissions } from '../../lib/permissions';
import { useNavigate } from '@tanstack/react-router';
import { accountApi } from '../account/accountApi';
import type { GitChange, GitCommit } from './git';
import { expectBuild, gitApi } from './git';
import { MergeDialog } from './MergeDialog';
import { RELEASE_BRANCH } from './constants';
import { useDiffStats } from './useDiffStats';
import { useRelativeTime } from './useRelativeTime';
import { useVfs } from './vfs';
import type { DiffStat } from '@dcms/gjs-parse/diff';

// A file in conflict when committing onto a branch that moved: the branch's current
// version vs the user's edit (with the common ancestor for a 3-way view).
interface CommitConflictFile {
  path: string;
  branchContent: string | null;
  draftContent: string | null;
  baseContent?: string | null;
}

// The Source Control view that lives in the IDE's left sidebar (VS Code style):
// a branch switcher + create, a commit box, the working-draft changes list (clicking
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
  const relative = useRelativeTime();
  const queryClient = useQueryClient();
  const openDiff = useVfs((s) => s.openDiff);
  const [message, setMessage] = useState('');
  const [description, setDescription] = useState('');
  const [newBranchName, setNewBranchName] = useState('');
  const [showNewBranch, setShowNewBranch] = useState(false);
  const [creatingBranch, setCreatingBranch] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  const [mergeOpen, setMergeOpen] = useState(false);
  const [conflict, setConflict] = useState<CommitConflictFile[] | null>(null);
  const [showHistory, setShowHistory] = useState(true);
  const [showClone, setShowClone] = useState(false);

  const { data: me } = useMyPermissions(true);
  // Whether the caller may clone/pull this site's repo with their own credentials.
  // Mirrors the server's RepoAccessReconciler: site editors (site:edit / site:publish,
  // held by Owner/admin roles and SuperAdmins) get repo access, plus anyone with an
  // explicit per-site repo:{siteId}:read|write grant.
  const canClone =
    can(me, Perm.SiteEdit) ||
    can(me, Perm.SitePublish) ||
    can(me, repoRead(siteId)) ||
    can(me, repoWrite(siteId));
  const navigate = useNavigate();
  // Only fetch git-credential status once the clone panel is opened by a user who
  // actually has repo access — used to nudge Google/no-password accounts.
  const account = useQuery({
    queryKey: ['account-me'],
    queryFn: () => accountApi.me(),
    enabled: showClone && canClone,
  });
  const needsGitCreds = account.data && !account.data.hasGitPassword;

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

  const changeList = changes.data ?? [];
  // Line counts per changed file. Computed in a worker, so a draft with a few
  // large files does not stall the panel every time it refreshes.
  const diffStats = useDiffStats(changeList);

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
    mutationFn: (v: { toNewBranch: boolean; resolve?: 'mine' | 'theirs' }) =>
      gitApi.commit(siteId, {
        branch,
        message: message.trim(),
        description: description.trim() || undefined,
        newBranch: v.toNewBranch ? newBranchName.trim() : undefined,
        resolve: v.resolve,
      }),
    onSuccess: (res, v) => {
      toast.success(t('ide.git.committed'));
      setConflict(null);
      afterCommit();
      if (v.toNewBranch) onSwitchBranch(res.branch);
      else onReload();
    },
    onError: (e) => {
      // The branch moved AND the same files were edited on both sides — the only case
      // reconcile can't resolve alone. Ask the user which version to keep (showing content).
      if (e instanceof ApiError && e.status === 409) {
        const files = (e.detail as { files?: CommitConflictFile[] })?.files ?? [];
        if (files.length) setConflict(files);
        else toast.error(t('ide.git.branchMoved'));
      } else {
        toast.error(t('errors.generic'));
      }
    },
  });

  const createBranch = useMutation({
    mutationFn: (name: string) => gitApi.createBranch(siteId, name, branch),
    onSuccess: (res) => {
      toast.success(t('ide.git.branchCreated'));
      setShowCreate(false);
      setCreatingBranch('');
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

  const askRestore = (c: GitCommit) => {
    if (window.confirm(t('ide.git.restoreConfirm', { sha: c.shortSha }))) restore.mutate(c.sha);
  };

  if (status.isLoading) return <CenteredSpinner label={t('common.loading')} />;
  const s = status.data;
  if (s && !s.enabled) return <p className="p-3 text-sm text-muted-foreground">{t('ide.git.disabled')}</p>;
  if (s?.enabled && !provisioned)
    return <p className="p-3 text-sm text-muted-foreground">{t('ide.git.provisioning')}</p>;

  const branchList = branches.data ?? [{ name: branch }];
  const onRelease = branch === RELEASE_BRANCH;
  const submitCreate = () => {
    const name = creatingBranch.trim();
    if (name && !createBranch.isPending) createBranch.mutate(name);
  };

  return (
    <div className="flex h-full flex-col overflow-auto">
      {/* Branch bar */}
      <div className="shrink-0 border-b p-2">
        <DropdownMenu>
          <DropdownMenuTrigger
            className="flex w-full items-center gap-2 rounded border bg-background px-2 py-1.5 text-left text-xs outline-none transition-colors hover:bg-accent focus:ring-1 focus:ring-ring"
            title={t('ide.git.switchBranch')}
          >
            <GitBranch className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
            <span className="min-w-0 flex-1 truncate font-medium">{branch}</span>
            {onRelease && <BranchTag label={t('ide.git.production')} tone="release" />}
            <ChevronDown className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start" className="min-w-52">
            <DropdownMenuLabel className="px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              {t('ide.git.branch')}
            </DropdownMenuLabel>
            {branchList.map((b) => (
              <DropdownMenuItem
                key={b.name}
                onSelect={() => b.name !== branch && onSwitchBranch(b.name)}
                className="text-xs"
              >
                <Check className={cn('h-3.5 w-3.5 shrink-0', b.name === branch ? 'opacity-100' : 'opacity-0')} />
                <span className="min-w-0 flex-1 truncate">{b.name}</span>
                {b.name === RELEASE_BRANCH && <BranchTag label={t('ide.git.production')} tone="release" />}
              </DropdownMenuItem>
            ))}
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => setShowCreate(true)} className="text-xs">
              <Plus className="h-3.5 w-3.5 shrink-0" />
              {t('ide.git.createBranchAction')}
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>

        {showCreate && (
          <div className="mt-2 flex items-center gap-1.5">
            {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
            <input
              autoFocus
              value={creatingBranch}
              onChange={(e) => setCreatingBranch(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') submitCreate();
                if (e.key === 'Escape') setShowCreate(false);
              }}
              placeholder={t('ide.git.newBranchName')}
              className="min-w-0 flex-1 rounded border bg-background px-2 py-1 text-xs outline-none focus:ring-1 focus:ring-ring"
            />
            <Button size="icon" variant="ghost" title={t('ide.git.createBranch')} disabled={!creatingBranch.trim() || createBranch.isPending} onClick={submitCreate}>
              <Check className="h-4 w-4" />
            </Button>
            <Button size="icon" variant="ghost" title={t('actions.cancel')} onClick={() => setShowCreate(false)}>
              <X className="h-4 w-4" />
            </Button>
          </div>
        )}
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
            onClick={() => commit.mutate({ toNewBranch: showNewBranch })}
          >
            <GitCommitHorizontal className="h-4 w-4 shrink-0" />
            <span className="truncate">
              {showNewBranch ? t('ide.git.commitToNewBranch') : t('ide.git.commitTo', { branch })}
            </span>
          </Button>
          <Button
            variant={showNewBranch ? 'default' : 'outline'}
            size="icon"
            title={t('ide.git.newBranch')}
            aria-pressed={showNewBranch}
            onClick={() => setShowNewBranch((v) => !v)}
          >
            <GitBranch className="h-4 w-4" />
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
                <DiffCount stat={diffStats[c.path]} />
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
            {history.data?.map((c, i) => (
              <li key={c.sha} className="group flex items-start gap-1.5 px-2 py-1.5 hover:bg-accent/40">
                <GitCommitHorizontal className="mt-0.5 h-3 w-3 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-xs">{(c.message ?? '').split('\n')[0]}</p>
                  <p className="flex items-center gap-1.5 text-[10px] text-muted-foreground">
                    <code>{c.shortSha}</code>
                    {c.author ? <span className="truncate">· {c.author}</span> : null}
                    {c.date ? (
                      <span className="shrink-0" title={new Date(c.date).toLocaleString()}>
                        · {relative(c.date)}
                      </span>
                    ) : null}
                    {i === 0 && <BranchTag label={t('ide.git.current')} tone="current" />}
                  </p>
                </div>
                <div className="flex shrink-0 items-center gap-0.5 opacity-0 transition-opacity focus-within:opacity-100 group-hover:opacity-100">
                  <button
                    type="button"
                    onClick={() => askRestore(c)}
                    disabled={restore.isPending}
                    title={t('ide.git.restore')}
                    className="rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground disabled:opacity-50"
                  >
                    <RotateCcw className="h-3.5 w-3.5" />
                  </button>
                  {c.htmlUrl && (
                    <a
                      href={c.htmlUrl}
                      target="_blank"
                      rel="noreferrer"
                      title={t('ide.git.viewInForgejo')}
                      className="rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
                    >
                      <ExternalLink className="h-3.5 w-3.5" />
                    </a>
                  )}
                </div>
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
            {canClone ? (
              <>
                <CopyRow label="HTTPS" value={s.httpUrl ?? ''} />
                <CopyRow label="SSH" value={s.sshUrl ?? ''} />
                {needsGitCreds ? (
                  <button
                    type="button"
                    onClick={() => void navigate({ to: '/account' as string })}
                    className="mt-1 w-full rounded border border-amber-500/40 bg-amber-500/10 px-2 py-1.5 text-left text-xs text-amber-700 hover:bg-amber-500/20 dark:text-amber-400"
                  >
                    {t('ide.git.noGitPassword')}
                  </button>
                ) : null}
              </>
            ) : (
              <p className="px-1 text-xs text-muted-foreground">{t('ide.git.noRepoAccess')}</p>
            )}
          </div>
        )}
      </div>

      <MergeDialog
        siteId={siteId}
        head={branch}
        open={mergeOpen}
        onOpenChange={setMergeOpen}
        onMerged={() => {
          // A merge into `release` builds. Nothing here told the Deployments panel that, so a
          // publish started from this view left it showing the previous deployment until the
          // user reloaded -- the same gap the IDE toolbar's publish had.
          expectBuild(siteId);
          queryClient.invalidateQueries({ queryKey: ['git-history', siteId] });
          queryClient.invalidateQueries({ queryKey: ['git-changes', siteId] });
          queryClient.invalidateQueries({ queryKey: ['site-builds', siteId] });
        }}
      />

      {/* Commit conflict: the branch moved and the same files were edited on both
          sides. Everything else was merged automatically — the user just picks which
          version wins for these files, then the commit goes through. */}
      <Dialog open={!!conflict} onOpenChange={(o) => !o && setConflict(null)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>{t('ide.git.conflictTitle')}</DialogTitle>
          </DialogHeader>
          <DialogBody className="space-y-3">
            <p className="text-sm text-muted-foreground">
              {t('ide.git.conflictIntroCommit', { branch })}
            </p>
            <ul className="max-h-48 space-y-1 overflow-auto rounded border bg-muted/40 p-2">
              {(conflict ?? []).map((f) => (
                <li key={f.path} className="flex items-center gap-1.5 text-xs">
                  <FilePen className="h-3.5 w-3.5 shrink-0 text-amber-600" />
                  <span className="min-w-0 flex-1 truncate" title={f.path}>{f.path}</span>
                  <button
                    type="button"
                    onClick={() =>
                      openDiff({
                        path: f.path,
                        status: 'modified',
                        original: f.branchContent,
                        modified: f.draftContent,
                      })
                    }
                    title={t('ide.git.viewDiff')}
                    className="shrink-0 rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
                  >
                    <Eye className="h-3.5 w-3.5" />
                  </button>
                </li>
              ))}
            </ul>
          </DialogBody>
          <DialogFooter className="flex-col gap-2 sm:flex-row">
            <Button variant="ghost" onClick={() => setConflict(null)} disabled={commit.isPending}>
              {t('actions.cancel')}
            </Button>
            <Button
              variant="outline"
              onClick={() => commit.mutate({ toNewBranch: false, resolve: 'theirs' })}
              disabled={commit.isPending}
            >
              {t('ide.git.useBranchVersion', { branch })}
            </Button>
            <Button
              onClick={() => commit.mutate({ toNewBranch: false, resolve: 'mine' })}
              disabled={commit.isPending}
            >
              {t('ide.git.keepMyChanges')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

function BranchTag({ label, tone }: { label: string; tone: 'release' | 'current' }) {
  return (
    <span
      className={cn(
        'shrink-0 rounded px-1 py-px text-[9px] font-semibold uppercase tracking-wide',
        tone === 'release'
          ? 'bg-amber-500/15 text-amber-600 dark:text-amber-400'
          : 'bg-primary/15 text-primary',
      )}
    >
      {label}
    </span>
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

/** `+12 −3` beside a changed file. Absent until the worker has answered. */
function DiffCount({ stat }: { stat: DiffStat | undefined }) {
  if (!stat || (stat.added === 0 && stat.removed === 0)) return null;
  // An approximate stat is a rewrite the diff gave up on splitting precisely;
  // the tilde says the numbers are file sizes, not an edit count.
  const prefix = stat.approximate ? '~' : '';
  return (
    <span className="shrink-0 font-mono text-[10px] tabular-nums">
      {stat.added > 0 && <span className="text-green-600">{`${prefix}+${stat.added}`}</span>}
      {stat.added > 0 && stat.removed > 0 && ' '}
      {stat.removed > 0 && <span className="text-destructive">{`${prefix}−${stat.removed}`}</span>}
    </span>
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
