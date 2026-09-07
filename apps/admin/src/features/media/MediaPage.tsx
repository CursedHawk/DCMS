import { Folder, FolderInput, Image as ImageIcon, Search, Trash2, UploadCloud, X } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  CenteredSpinner,
  cn,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  EmptyState,
  Input,
  Page,
  PageHeader,
  toastApiError,
} from '@dcms/ui';
import { FolderRail } from './FolderRail';
import { MediaDetailDialog } from './MediaDetailDialog';
import { MediaThumb } from './MediaThumb';
import { assetDragProps, folderDropProps } from './dnd';
import { MediaUploader } from './MediaUploader';
import { StorageMeter, categoryMeta } from './StorageMeter';
import {
  ROOT_FOLDER,
  type MediaStatus,
  formatDate,
  formatSize,
  useDeleteAssets,
  useMedia,
  useMediaFolders,
  useMediaUsage,
  useMoveAssets,
} from './api';

const statusTone: Record<MediaStatus, 'success' | 'warning' | 'destructive' | 'secondary'> = {
  Ready: 'success',
  Processing: 'warning',
  Uploaded: 'secondary',
  Failed: 'destructive',
};

export function MediaPage() {
  const { t } = useTranslation();
  const [folder, setFolder] = useState<string | undefined>(undefined);
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [detailId, setDetailId] = useState<string | null>(null);
  const [showUpload, setShowUpload] = useState(true);

  const media = useMedia(folder);
  const folders = useMediaFolders();
  const usage = useMediaUsage();
  const move = useMoveAssets();
  const del = useDeleteAssets();

  const folderList = folders.data ?? [];

  /*
   * Folders shown as tiles at the head of the grid, so the library browses like a
   * file manager rather than only through the rail. "All media" shows the
   * top-level folders; inside a folder you see its children. The Unfiled view is
   * by definition folder-less, and the search results are a flat list of assets,
   * so neither shows tiles.
   */
  const folderTiles = useMemo(() => {
    if (folder === ROOT_FOLDER || search.trim()) return [];
    const parentId = folder ?? null;
    return folderList.filter((f) => (f.parentId ?? null) === parentId);
  }, [folderList, folder, search]);
  const items = useMemo(() => {
    const q = search.trim().toLowerCase();
    const list = media.data ?? [];
    return q ? list.filter((a) => a.fileName.toLowerCase().includes(q)) : list;
  }, [media.data, search]);

  // Clear selections that are no longer visible (folder switch / filter change).
  const visibleIds = new Set(items.map((a) => a.id));
  const activeSelection = [...selected].filter((id) => visibleIds.has(id));

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const clearSelection = () => setSelected(new Set());

  /*
   * Which folder a drag is currently over.
   *
   * Kept here rather than inside each tile so that only one can be lit at a time — `dragleave`
   * on one tile and `dragenter` on the next arrive in that order, and per-tile state briefly
   * lights both.
   */
  const [dropFolder, setDropFolder] = useState<string | null>(null);

  /** Move whatever was dragged, which is not necessarily what is selected. */
  const dropOnto = (folderId: string | null, ids: string[]) => {
    if (ids.length === 0) return;
    move.mutate(
      { ids, folderId },
      {
        onSuccess: () => {
          toast.success(t('media.movedCount', { count: ids.length }));
          clearSelection();
        },
        onError: (e) => toastApiError(e, t),
      },
    );
  };

  const bulkMove = (folderId: string | null) => {
    if (activeSelection.length === 0) return;
    move.mutate(
      { ids: activeSelection, folderId },
      { onSuccess: clearSelection, onError: () => toast.error(t('errors.generic')) },
    );
  };

  const bulkDelete = () => {
    if (activeSelection.length === 0) return;
    if (!window.confirm(t('media.deleteManyConfirm', { count: activeSelection.length }))) return;
    del.mutate(activeSelection, {
      onSuccess: clearSelection,
      onError: () => toast.error(t('errors.generic')),
    });
  };

  const currentFolderName =
    folder === undefined
      ? t('media.folders.all')
      : folder === ROOT_FOLDER
        ? t('media.folders.unfiled')
        : (folderList.find((f) => f.id === folder)?.name ?? t('media.folders.all'));

  const hasSelection = activeSelection.length > 0;

  return (
    <Page className="max-w-7xl">
      <PageHeader
        title={t('media.title')}
        description={t('media.subtitle')}
        actions={
          <Button variant={showUpload ? 'secondary' : 'default'} onClick={() => setShowUpload((s) => !s)}>
            <UploadCloud className="h-4 w-4" /> {t('media.upload')}
          </Button>
        }
      />

      <div className="mb-5">
        <StorageMeter usage={usage.data} isLoading={usage.isLoading} />
      </div>

      <div className="grid gap-6 lg:grid-cols-[220px_minmax(0,1fr)]">
        <aside className="lg:sticky lg:top-6 lg:self-start">
          {folders.isLoading ? (
            <CenteredSpinner />
          ) : (
            <FolderRail
            onMove={dropOnto}
              folders={folderList}
              value={folder}
              onSelect={(v) => {
                setFolder(v);
                clearSelection();
              }}
              totalCount={usage.data?.assetCount ?? 0}
            />
          )}
        </aside>

        <section className="min-w-0 space-y-4">
          {showUpload ? <MediaUploader folderId={folder && folder !== ROOT_FOLDER ? folder : null} /> : null}

          {/* Toolbar: title of current view + search, or the bulk-action bar. */}
          {hasSelection ? (
            <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-accent/40 px-3 py-2">
              <span className="text-sm font-medium">
                {t('media.selectedCount', { count: activeSelection.length })}
              </span>
              <div className="ml-auto flex items-center gap-2">
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button size="sm" variant="outline">
                      <FolderInput className="h-4 w-4" /> {t('media.moveTo')}
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end">
                    <DropdownMenuLabel>{t('media.moveTo')}</DropdownMenuLabel>
                    <DropdownMenuItem onSelect={() => bulkMove(null)}>{t('media.folders.unfiled')}</DropdownMenuItem>
                    {folderList.length > 0 ? <DropdownMenuSeparator /> : null}
                    {folderList.map((f) => (
                      <DropdownMenuItem key={f.id} onSelect={() => bulkMove(f.id)}>
                        {f.name}
                      </DropdownMenuItem>
                    ))}
                  </DropdownMenuContent>
                </DropdownMenu>
                <Button size="sm" variant="destructive" onClick={bulkDelete} disabled={del.isPending}>
                  <Trash2 className="h-4 w-4" /> {t('common.delete')}
                </Button>
                <Button size="sm" variant="ghost" onClick={clearSelection}>
                  <X className="h-4 w-4" />
                </Button>
              </div>
            </div>
          ) : (
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="text-sm font-semibold">
                {currentFolderName}
                <span className="ml-2 font-normal text-muted-foreground">{items.length}</span>
              </h2>
              <div className="relative w-full max-w-xs">
                <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder={t('media.searchPlaceholder')}
                  className="h-9 pl-8"
                />
              </div>
            </div>
          )}

          {media.isLoading ? (
            <CenteredSpinner />
          ) : items.length > 0 || folderTiles.length > 0 ? (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
              {folderTiles.map((f) => (
                <button
                  key={f.id}
                  type="button"
                  onClick={() => {
                    setFolder(f.id);
                    clearSelection();
                  }}
                  {...folderDropProps({
                    onDropAssets: (ids) => dropOnto(f.id, ids),
                    onOver: () => setDropFolder(f.id),
                    onLeave: () => setDropFolder((current) => (current === f.id ? null : current)),
                  })}
                  className={cn(
                    'flex flex-col items-start gap-3 rounded-lg border bg-card p-4 text-left transition-shadow hover:border-primary/50 hover:shadow-md',
                    dropFolder === f.id && 'border-primary ring-2 ring-primary/40',
                  )}
                >
                  <span className="flex h-12 w-12 items-center justify-center rounded-lg bg-accent text-accent-foreground">
                    <Folder className="h-6 w-6" />
                  </span>
                  <span className="min-w-0 w-full">
                    <span className="block truncate text-sm font-medium" title={f.name}>
                      {f.name}
                    </span>
                    <span className="block text-[11px] text-muted-foreground">
                      {t('media.folders.itemCount', { count: f.assetCount })}
                    </span>
                  </span>
                </button>
              ))}
              {items.map((a) => {
                const isSelected = selected.has(a.id);
                const CatIcon = categoryMeta[a.category].icon;
                const saved =
                  a.variantBytes > 0 && a.variantBytes < a.sizeBytes
                    ? Math.round((1 - a.variantBytes / a.sizeBytes) * 100)
                    : 0;
                return (
                  <div
                    key={a.id}
                    {...assetDragProps(a.id, selected)}
                    className={cn(
                      'group relative overflow-hidden rounded-lg border bg-card transition-shadow hover:shadow-md',
                      isSelected && 'ring-2 ring-primary',
                    )}
                  >
                    {/* Selection checkbox — always visible once anything is selected. */}
                    <button
                      type="button"
                      onClick={() => toggle(a.id)}
                      aria-label={t('media.select')}
                      className={cn(
                        'absolute left-2 top-2 z-10 flex h-5 w-5 items-center justify-center rounded border bg-background/90 transition-opacity',
                        isSelected
                          ? 'border-primary bg-primary text-primary-foreground opacity-100'
                          : 'opacity-0 group-hover:opacity-100',
                      )}
                    >
                      {isSelected ? <span className="text-[11px] font-bold">✓</span> : null}
                    </button>

                    <button
                      type="button"
                      onClick={() => setDetailId(a.id)}
                      className="block aspect-square w-full overflow-hidden"
                    >
                      <MediaThumb id={a.id} category={a.category} status={a.status} />
                    </button>

                    <div className="space-y-1 p-2.5">
                      <p className="flex items-center gap-1 truncate text-xs font-medium" title={a.fileName}>
                        <CatIcon className="h-3 w-3 shrink-0 text-muted-foreground" />
                        <span className="truncate">{a.fileName}</span>
                      </p>
                      <div className="flex items-center justify-between text-[11px] text-muted-foreground">
                        <span>{formatDate(a.createdAt)}</span>
                        <span className="tabular-nums">{formatSize(a.sizeBytes)}</span>
                      </div>
                      <div className="flex items-center justify-between">
                        <Badge tone={statusTone[a.status]} className="text-[10px]">
                          {t(`media.${a.status.toLowerCase()}`, a.status)}
                        </Badge>
                        {saved > 0 ? (
                          <span
                            className="text-[10px] font-medium text-emerald-600 dark:text-emerald-400"
                            title={t('media.optimizedHint')}
                          >
                            −{saved}%
                          </span>
                        ) : null}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          ) : (
            <EmptyState
              icon={ImageIcon}
              title={search ? t('media.noResults') : t('media.emptyTitle')}
              description={search ? t('media.noResultsHint') : t('media.dropHere')}
            />
          )}
        </section>
      </div>

      <MediaDetailDialog
        assetId={detailId}
        folders={folderList}
        onOpenChange={(open) => !open && setDetailId(null)}
        onDeleted={() => setDetailId(null)}
      />
    </Page>
  );
}
