import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BookOpen, Folder, FolderOpen, Pencil, Plus, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  CenteredSpinner,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  EmptyState,
  Input,
  Label,
  LoadFailed,
  Page,
  PageHeader,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Textarea,
  cn,
  toastApiError,
  useConfirm,
  usePermissions,
} from '@dcms/ui';
import { AgentMarkdown } from '../../components/AgentMarkdown';
import { api } from '../../lib/api';
import { can, Perm } from '../../lib/permissions';
import {
  type KnowledgeScope,
  type KnowledgeSummary,
  knowledgeApi,
  knowledgeKey,
  normalisePath,
} from '../agent/knowledge';

interface SiteRef {
  id: string;
  name: string;
}

/** `all` = every scope; `workspace` = workspace-wide only; otherwise a site id. */
type Filter = 'all' | 'workspace' | string;

/**
 * The workspace's AI knowledge store (ADR 0034): what agents saved — decisions, setups,
 * information, archives of compacted conversations — and what people added. Agents read it only
 * when they ask; this is where people see, correct and prune it.
 */
export function KnowledgePage() {
  const { t } = useTranslation();
  const me = usePermissions();
  const mayWrite = can(me, Perm.AiKnowledge);
  const confirm = useConfirm();
  const qc = useQueryClient();
  const [filter, setFilter] = useState<Filter>('all');
  const [folder, setFolder] = useState('');
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState<KnowledgeSummary | 'new' | null>(null);

  const sites = useQuery({ queryKey: ['sites'], queryFn: () => api.get<SiteRef[]>('/admin/sites') });
  const siteName = (id: string | null) => (id ? (sites.data?.find((s) => s.id === id)?.name ?? id) : null);

  const scope: KnowledgeScope = filter === 'all' ? { allSites: true } : filter === 'workspace' ? {} : { siteId: filter };
  const listing = useQuery({
    queryKey: [knowledgeKey, filter, search],
    queryFn: () => (search.trim() ? knowledgeApi.search(scope, search.trim()) : knowledgeApi.tree(scope)),
  });

  const remove = useMutation({
    mutationFn: (id: string) => knowledgeApi.remove(id),
    onSuccess: async () => {
      toast.success(t('knowledge.removed'));
      await qc.invalidateQueries({ queryKey: [knowledgeKey] });
    },
    onError: (e) => toastApiError(e, t),
  });

  const entries = listing.data?.entries ?? [];
  const folders = useMemo(() => folderCounts(entries), [entries]);
  const shown = search.trim() ? entries : entries.filter((e) => inFolder(e.path, folder));

  return (
    <Page>
      <PageHeader
        title={t('knowledge.title')}
        description={t('knowledge.description')}
        actions={
          mayWrite ? (
            <Button onClick={() => setOpen('new')}>
              <Plus className="h-4 w-4" /> {t('knowledge.add')}
            </Button>
          ) : undefined
        }
      />

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Select value={filter} onValueChange={(v) => setFilter(v)}>
          <SelectTrigger className="w-56" aria-label={t('knowledge.scope')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">{t('knowledge.allScopes')}</SelectItem>
            <SelectItem value="workspace">{t('knowledge.workspaceOnly')}</SelectItem>
            {(sites.data ?? []).map((site) => (
              <SelectItem key={site.id} value={site.id}>
                {site.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Input
          className="w-64"
          type="search"
          placeholder={t('knowledge.search')}
          aria-label={t('knowledge.search')}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      {listing.isLoading ? (
        <CenteredSpinner />
      ) : listing.isError ? (
        <LoadFailed onRetry={() => void listing.refetch()} />
      ) : entries.length === 0 ? (
        <EmptyState icon={BookOpen} title={t('knowledge.none')} description={t('knowledge.noneHint')} />
      ) : (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-[14rem_1fr]">
          {search.trim() ? null : (
            <nav aria-label={t('knowledge.folders')} className="space-y-0.5 text-sm">
              <FolderButton label={t('knowledge.everything')} depth={0} active={folder === ''} count={entries.length} onClick={() => setFolder('')} />
              {folders.map((f) => (
                <FolderButton
                  key={f.path}
                  label={f.path.split('/').at(-1)!}
                  depth={f.path.split('/').length}
                  active={folder === f.path}
                  count={f.count}
                  onClick={() => setFolder(f.path)}
                />
              ))}
            </nav>
          )}
          <ul className={cn('divide-y rounded-md border', search.trim() && 'md:col-span-2')}>
            {shown.map((entry) => (
              <li key={entry.id} className="flex items-start gap-2 px-3 py-2">
                <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setOpen(entry)}>
                  <span className="flex flex-wrap items-center gap-1.5">
                    <span className="font-medium [overflow-wrap:anywhere]">{entry.title}</span>
                    <Badge tone="secondary">{t(`knowledge.source.${entry.source}`)}</Badge>
                    <Badge tone="default">{siteName(entry.siteId) ?? t('knowledge.workspace')}</Badge>
                  </span>
                  <code className="block text-xs text-muted-foreground [overflow-wrap:anywhere]">{entry.path}</code>
                  <span className="block text-sm text-muted-foreground">{entry.description}</span>
                </button>
                {mayWrite ? (
                  <Button
                    size="icon"
                    variant="ghost"
                    title={t('actions.delete')}
                    aria-label={t('knowledge.deleteEntry', { path: entry.path })}
                    onClick={() =>
                      void confirm({ title: t('knowledge.confirmDelete', { path: entry.path }), destructive: true }).then(
                        (ok) => ok && remove.mutate(entry.id),
                      )
                    }
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        </div>
      )}

      {open ? (
        <EntryDialog
          entry={open === 'new' ? null : open}
          sites={sites.data ?? []}
          defaultFolder={folder}
          mayWrite={mayWrite}
          onClose={() => setOpen(null)}
        />
      ) : null}
    </Page>
  );
}

function FolderButton({
  label,
  depth,
  active,
  count,
  onClick,
}: {
  label: string;
  depth: number;
  active: boolean;
  count: number;
  onClick: () => void;
}) {
  const Icon = active ? FolderOpen : Folder;
  return (
    <button
      type="button"
      onClick={onClick}
      aria-current={active ? 'true' : undefined}
      style={{ paddingLeft: `${0.5 + depth * 0.75}rem` }}
      className={cn(
        'flex w-full items-center gap-1.5 rounded-md py-1 pr-2 text-left hover:bg-accent',
        active && 'bg-accent font-medium',
      )}
    >
      <Icon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="min-w-0 flex-1 truncate">{label}</span>
      <span className="text-xs text-muted-foreground">{count}</span>
    </button>
  );
}

function EntryDialog({
  entry,
  sites,
  defaultFolder,
  mayWrite,
  onClose,
}: {
  entry: KnowledgeSummary | null;
  sites: readonly SiteRef[];
  defaultFolder: string;
  mayWrite: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const full = useQuery({
    queryKey: [knowledgeKey, 'entry', entry?.id],
    queryFn: () => knowledgeApi.read(entry!.path, entry!.siteId),
    enabled: !!entry,
  });
  const loaded = entry ? full.data : null;
  const [editing, setEditing] = useState(!entry);
  const [draft, setDraft] = useState<{ path: string; title: string; description: string; body: string; siteId: string | null } | null>(
    entry ? null : { path: defaultFolder ? `${defaultFolder}/` : 'decisions/', title: '', description: '', body: '', siteId: null },
  );
  const [preview, setPreview] = useState(false);

  const value = draft ?? (loaded ? { ...loaded } : null);
  const save = useMutation({
    mutationFn: () =>
      knowledgeApi.save({
        siteId: value!.siteId,
        path: normalisePath(value!.path),
        title: value!.title.trim(),
        description: value!.description.trim(),
        body: value!.body,
        source: 'member',
        expectedUpdatedAt: loaded && loaded.path === normalisePath(value!.path) && loaded.siteId === value!.siteId ? loaded.updatedAt : null,
      }),
    onSuccess: async () => {
      toast.success(t('knowledge.saved'));
      await qc.invalidateQueries({ queryKey: [knowledgeKey] });
      onClose();
    },
    onError: (e) => toastApiError(e, t),
  });

  const change = (patch: Partial<NonNullable<typeof value>>) => setDraft({ ...value!, ...patch });
  const valid = !!value && normalisePath(value.path) !== '' && value.title.trim() !== '' && value.description.trim() !== '';

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>{entry ? entry.title : t('knowledge.add')}</DialogTitle>
        </DialogHeader>

        {!value ? (
          <CenteredSpinner />
        ) : editing ? (
          <div className="space-y-3">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div className="space-y-1.5">
                <Label htmlFor="knowledge-path">{t('knowledge.path')}</Label>
                <Input id="knowledge-path" value={value.path} onChange={(e) => change({ path: e.target.value })} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="knowledge-scope">{t('knowledge.scope')}</Label>
                <Select value={value.siteId ?? 'workspace'} onValueChange={(v) => change({ siteId: v === 'workspace' ? null : v })}>
                  <SelectTrigger id="knowledge-scope">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="workspace">{t('knowledge.workspace')}</SelectItem>
                    {sites.map((site) => (
                      <SelectItem key={site.id} value={site.id}>
                        {site.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="knowledge-title">{t('knowledge.entryTitle')}</Label>
              <Input id="knowledge-title" value={value.title} onChange={(e) => change({ title: e.target.value })} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="knowledge-description">{t('knowledge.entryDescription')}</Label>
              <Input id="knowledge-description" value={value.description} onChange={(e) => change({ description: e.target.value })} />
            </div>
            <div className="space-y-1.5">
              <div className="flex items-center justify-between">
                <Label htmlFor="knowledge-body">{t('knowledge.body')}</Label>
                <Button size="sm" variant="ghost" onClick={() => setPreview((p) => !p)}>
                  {preview ? t('knowledge.edit') : t('knowledge.preview')}
                </Button>
              </div>
              {preview ? (
                <div className="max-h-96 overflow-y-auto rounded-md border p-3">
                  <AgentMarkdown text={value.body} />
                </div>
              ) : (
                <Textarea
                  id="knowledge-body"
                  className="min-h-64 font-mono text-xs"
                  value={value.body}
                  onChange={(e) => change({ body: e.target.value })}
                />
              )}
            </div>
          </div>
        ) : (
          <div className="space-y-2">
            <code className="block text-xs text-muted-foreground">{value.path}</code>
            <p className="text-sm text-muted-foreground">{value.description}</p>
            <div className="max-h-[60vh] overflow-y-auto rounded-md border p-3">
              <AgentMarkdown text={value.body} />
            </div>
          </div>
        )}

        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            {t('actions.close')}
          </Button>
          {mayWrite && !editing ? (
            <Button variant="outline" onClick={() => setEditing(true)}>
              <Pencil className="h-4 w-4" /> {t('knowledge.edit')}
            </Button>
          ) : null}
          {mayWrite && editing ? (
            <Button disabled={!valid || save.isPending} onClick={() => save.mutate()}>
              {t('actions.save')}
            </Button>
          ) : null}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Every folder in the listing with how many entries it holds, in path order. */
function folderCounts(entries: readonly KnowledgeSummary[]): { path: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const entry of entries) {
    const segments = entry.path.split('/').slice(0, -1);
    for (let i = 1; i <= segments.length; i++) {
      const path = segments.slice(0, i).join('/');
      counts.set(path, (counts.get(path) ?? 0) + 1);
    }
  }
  return [...counts.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([path, count]) => ({ path, count }));
}

function inFolder(path: string, folder: string): boolean {
  return folder === '' || path.startsWith(`${folder}/`);
}

export default KnowledgePage;
