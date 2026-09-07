import { Check, FolderPlus, Folder, Images, Inbox, MoreVertical, Pencil, Trash2, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  cn,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  Input,
} from '@dcms/ui';
import { ROOT_FOLDER, type MediaFolder, useCreateFolder, useDeleteFolder, useRenameFolder } from './api';
import { folderDropProps } from './dnd';

/**
 * Left rail listing the tenant's folders plus the "All media" / "Unfiled"
 * pseudo-folders. `value` is the active filter (undefined = all, ROOT_FOLDER =
 * unfiled, otherwise a folder id). Folders can be created, renamed and deleted
 * inline; deleting keeps the assets (they return to Unfiled).
 */
export function FolderRail({
  folders,
  value,
  onSelect,
  totalCount,
  onMove,
}: {
  folders: MediaFolder[];
  value: string | undefined;
  onSelect: (value: string | undefined) => void;
  totalCount: number;
  /** Move assets into a folder (`null` = unfiled). Omitted disables drop targets. */
  onMove?: (folderId: string | null, ids: string[]) => void;
}) {
  const { t } = useTranslation();
  /*
   * Which target a drag is currently over.
   *
   * One piece of state for the whole rail rather than one per row: `dragleave` on one row and
   * `dragenter` on the next arrive in that order, so per-row state briefly lights both.
   * `undefined` is "nothing", and `null` is a real target — the Unfiled row.
   */
  const [dropTarget, setDropTarget] = useState<string | null | undefined>(undefined);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');

  const create = useCreateFolder();
  const rename = useRenameFolder();
  const remove = useDeleteFolder();

  const filedCount = folders.reduce((sum, f) => sum + f.assetCount, 0);
  const unfiledCount = Math.max(0, totalCount - filedCount);

  const submitCreate = () => {
    const name = newName.trim();
    if (!name) return;
    create.mutate(
      { name },
      {
        onSuccess: () => {
          setNewName('');
          setCreating(false);
        },
        onError: () => toast.error(t('errors.generic')),
      },
    );
  };

  const submitRename = (id: string) => {
    const name = editName.trim();
    if (!name) {
      setEditingId(null);
      return;
    }
    rename.mutate(
      { id, name },
      {
        onSuccess: () => setEditingId(null),
        onError: () => toast.error(t('errors.generic')),
      },
    );
  };

  const del = (f: MediaFolder) => {
    if (!window.confirm(t('media.folders.deleteConfirm', { name: f.name }))) return;
    remove.mutate(f.id, {
      onSuccess: () => {
        if (value === f.id) onSelect(undefined);
      },
      onError: () => toast.error(t('errors.generic')),
    });
  };

  const RailButton = ({
    active,
    onClick,
    icon: Icon,
    label,
    count,
    dropFolderId,
  }: {
    active: boolean;
    onClick: () => void;
    icon: typeof Images;
    label: string;
    count: number;
    /** The folder assets dropped here move into. `null` unfiles them. Omit to refuse drops. */
    dropFolderId?: string | null;
  }) => (
    <button
      type="button"
      onClick={onClick}
      {...(dropFolderId !== undefined && onMove
        ? folderDropProps({
            onDropAssets: (ids) => onMove(dropFolderId, ids),
            onOver: () => setDropTarget(dropFolderId),
            onLeave: () =>
              setDropTarget((current) => (current === dropFolderId ? undefined : current)),
          })
        : {})}
      className={cn(
        'flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 text-sm transition-colors',
        active ? 'bg-accent font-medium text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50',
        dropFolderId !== undefined && dropTarget === dropFolderId && 'ring-2 ring-primary',
      )}
    >
      <Icon className="h-4 w-4 shrink-0" />
      <span className="flex-1 truncate text-left">{label}</span>
      <span className="text-xs tabular-nums opacity-70">{count}</span>
    </button>
  );

  return (
    <div className="space-y-0.5">
      <RailButton
        active={value === undefined}
        onClick={() => onSelect(undefined)}
        icon={Images}
        label={t('media.folders.all')}
        count={totalCount}
      />
      {/* Dropping onto Unfiled is how an asset comes back OUT of a folder — the only way
          there was before this involved a dropdown listing every folder except the one you
          wanted to leave. */}
      <RailButton
        active={value === ROOT_FOLDER}
        onClick={() => onSelect(ROOT_FOLDER)}
        icon={Inbox}
        label={t('media.folders.unfiled')}
        count={unfiledCount}
        dropFolderId={null}
      />

      <div className="!my-2 flex items-center justify-between px-2.5 pt-1">
        <span className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
          {t('media.folders.title')}
        </span>
        <button
          type="button"
          onClick={() => setCreating((c) => !c)}
          className="text-muted-foreground hover:text-foreground"
          aria-label={t('media.folders.new')}
          title={t('media.folders.new')}
        >
          <FolderPlus className="h-4 w-4" />
        </button>
      </div>

      {creating ? (
        <div className="flex items-center gap-1 px-1 pb-1">
          <Input
            autoFocus
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') submitCreate();
              if (e.key === 'Escape') setCreating(false);
            }}
            placeholder={t('media.folders.namePlaceholder')}
            className="h-8"
          />
          <Button size="icon" variant="ghost" className="h-8 w-8 shrink-0" onClick={submitCreate}>
            <Check className="h-4 w-4" />
          </Button>
        </div>
      ) : null}

      {folders.map((f) =>
        editingId === f.id ? (
          <div key={f.id} className="flex items-center gap-1 px-1">
            <Input
              autoFocus
              value={editName}
              onChange={(e) => setEditName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') submitRename(f.id);
                if (e.key === 'Escape') setEditingId(null);
              }}
              className="h-8"
            />
            <Button size="icon" variant="ghost" className="h-8 w-8 shrink-0" onClick={() => submitRename(f.id)}>
              <Check className="h-4 w-4" />
            </Button>
            <Button size="icon" variant="ghost" className="h-8 w-8 shrink-0" onClick={() => setEditingId(null)}>
              <X className="h-4 w-4" />
            </Button>
          </div>
        ) : (
          <div
            key={f.id}
            {...(onMove
              ? folderDropProps({
                  onDropAssets: (ids) => onMove(f.id, ids),
                  onOver: () => setDropTarget(f.id),
                  onLeave: () => setDropTarget((current) => (current === f.id ? undefined : current)),
                })
              : {})}
            className={cn(
              'group flex items-center gap-2 rounded-md px-2.5 py-1.5 text-sm transition-colors',
              value === f.id ? 'bg-accent font-medium text-accent-foreground' : 'text-muted-foreground hover:bg-accent/50',
              dropTarget === f.id && 'ring-2 ring-primary',
            )}
          >
            <button
              type="button"
              onClick={() => onSelect(f.id)}
              className="flex min-w-0 flex-1 items-center gap-2 text-left"
            >
              <Folder className="h-4 w-4 shrink-0" />
              <span className="flex-1 truncate">{f.name}</span>
              <span className="text-xs tabular-nums opacity-70">{f.assetCount}</span>
            </button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <button
                  type="button"
                  className="shrink-0 opacity-0 transition-opacity group-hover:opacity-100 data-[state=open]:opacity-100"
                  aria-label={t('common.actions')}
                >
                  <MoreVertical className="h-4 w-4" />
                </button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem
                  onSelect={() => {
                    setEditingId(f.id);
                    setEditName(f.name);
                  }}
                >
                  <Pencil className="h-4 w-4" /> {t('common.rename')}
                </DropdownMenuItem>
                <DropdownMenuItem destructive onSelect={() => del(f)}>
                  <Trash2 className="h-4 w-4" /> {t('common.delete')}
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        ),
      )}
    </div>
  );
}
