import {
  ChevronDown,
  ChevronRight,
  Download,
  FileArchive,
  FileImage,
  FilePlus2,
  FileCode2,
  Lock,
  Pencil,
  Trash2,
  Upload,
} from 'lucide-react';
import { useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { cn } from '../../lib/cn';
import { isPreviewableImage } from './binary';
import { downloadFile, downloadProjectZip, readUpload, type UploadResult } from './fileio';
import { isToolchainFile } from './paths';
import { useVfs } from './vfs';

// A VS Code-styled explorer over the flat file map. Folders are derived from
// the path segments; toolchain files render locked (read-only, no delete).
//
// Folders have no storage of their own, so every folder operation is a bulk
// operation over the files that share its prefix — see the store's
// deleteFolder/renameFolder.

/**
 * Marks a drag as coming from this tree rather than the OS. The payload is the
 * dragged path; the type alone is readable during `dragover` (the value is not),
 * which is what lets the external-upload drop zone step aside for an internal move.
 */
const DND_PATH = 'application/x-dcms-path';

function baseName(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

function parentOf(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

interface TreeNode {
  name: string;
  path: string;
  dir: boolean;
  children: TreeNode[];
}

function buildTree(paths: string[]): TreeNode[] {
  const root: TreeNode = { name: '', path: '', dir: true, children: [] };
  for (const path of paths) {
    const segs = path.split('/');
    let node = root;
    let acc = '';
    segs.forEach((seg, i) => {
      acc = acc ? `${acc}/${seg}` : seg;
      const isFile = i === segs.length - 1;
      let child = node.children.find((c) => c.name === seg && c.dir === !isFile);
      if (!child) {
        child = { name: seg, path: acc, dir: !isFile, children: [] };
        node.children.push(child);
      }
      node = child;
    });
  }
  const sort = (nodes: TreeNode[]): TreeNode[] => {
    nodes.sort((a, b) => (a.dir === b.dir ? a.name.localeCompare(b.name) : a.dir ? -1 : 1));
    for (const n of nodes) sort(n.children);
    return nodes;
  };
  return sort(root.children);
}

/** How many files a folder holds, at any depth — what a delete would take with it. */
function countFiles(node: TreeNode): number {
  return node.dir ? node.children.reduce((n, c) => n + countFiles(c), 0) : 1;
}

export function FileTree() {
  const { t } = useTranslation();
  const files = useVfs((s) => s.files);
  const activePath = useVfs((s) => s.activePath);
  const open = useVfs((s) => s.open);
  const createFile = useVfs((s) => s.createFile);
  const importFiles = useVfs((s) => s.importFiles);
  const deleteFile = useVfs((s) => s.deleteFile);
  const renameFile = useVfs((s) => s.renameFile);
  const deleteFolder = useVfs((s) => s.deleteFolder);
  const renameFolder = useVfs((s) => s.renameFolder);

  const tree = useMemo(() => buildTree(Object.keys(files)), [files]);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [adding, setAdding] = useState(false);
  const [newName, setNewName] = useState('');
  const [dragOver, setDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  // The in-flight internal drag. Kept in a ref because `dragover` may only read
  // the data *types*, not the payload, so the target cannot ask what it is over.
  const dragging = useRef<{ path: string; dir: boolean } | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);

  /** Whether `path` may be dropped into `folder` ('' = repo root). */
  const canDropInto = (folder: string): boolean => {
    const drag = dragging.current;
    if (!drag) return false;
    if (parentOf(drag.path) === folder) return false; // already there
    // A folder cannot swallow itself.
    return !(drag.dir && (folder === drag.path || folder.startsWith(`${drag.path}/`)));
  };

  const dropInto = (folder: string) => {
    const drag = dragging.current;
    dragging.current = null;
    setDropTarget(null);
    if (!drag || !canDropInto(folder)) return;
    const to = folder ? `${folder}/${baseName(drag.path)}` : baseName(drag.path);
    const moved = drag.dir ? renameFolder(drag.path, to) : renameFile(drag.path, to);
    if (!moved) toast.error(t('ide.moveFailed', { path: to }));
  };

  const dragProps = (path: string, dir: boolean) => ({
    draggable: true,
    onDragStart: (e: React.DragEvent) => {
      dragging.current = { path, dir };
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData(DND_PATH, path);
    },
    onDragEnd: () => {
      dragging.current = null;
      setDropTarget(null);
    },
  });

  /** Drop-target wiring for a folder row ('' = the tree background, i.e. root). */
  const dropProps = (folder: string) => ({
    onDragOver: (e: React.DragEvent) => {
      if (!e.dataTransfer.types.includes(DND_PATH) || !canDropInto(folder)) return;
      e.preventDefault();
      e.stopPropagation();
      e.dataTransfer.dropEffect = 'move';
      setDropTarget(folder);
    },
    onDragLeave: () => setDropTarget((cur) => (cur === folder ? null : cur)),
    onDrop: (e: React.DragEvent) => {
      if (!e.dataTransfer.types.includes(DND_PATH)) return;
      e.preventDefault();
      e.stopPropagation();
      dropInto(folder);
    },
  });

  const uploadFiles = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0) return;
    const results: UploadResult[] = [];
    const errors: string[] = [];
    for (const file of Array.from(fileList)) {
      try {
        results.push(await readUpload(file));
      } catch (e) {
        errors.push((e as Error).message);
      }
    }
    if (results.length > 0) {
      const { added, skipped } = importFiles(results);
      if (added > 0) toast.success(t('ide.uploaded', { count: added }));
      if (skipped.length > 0) toast.message(t('ide.uploadSkipped', { count: skipped.length }));
    }
    if (errors.length > 0) toast.error(errors.join('\n'));
  };

  const toggle = (path: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });

  const submitNew = () => {
    const name = newName.trim();
    if (name) createFile(name);
    setNewName('');
    setAdding(false);
  };

  const renderNode = (node: TreeNode, depth: number): React.ReactNode => {
    const pad = { paddingLeft: `${depth * 12 + 8}px` };
    if (node.dir) {
      const isCollapsed = collapsed.has(node.path);
      return (
        <div key={node.path}>
          <div
            className={cn(
              'group flex items-center gap-1 py-1 pr-2 text-xs',
              dropTarget === node.path ? 'bg-primary/15 ring-1 ring-inset ring-primary/50' : 'hover:bg-accent/40',
            )}
            style={pad}
            {...dragProps(node.path, true)}
            {...dropProps(node.path)}
          >
            <button
              type="button"
              onClick={() => toggle(node.path)}
              className="flex min-w-0 flex-1 items-center gap-1 text-muted-foreground hover:text-foreground"
              title={node.path}
            >
              {isCollapsed ? (
                <ChevronRight className="h-3.5 w-3.5 shrink-0" />
              ) : (
                <ChevronDown className="h-3.5 w-3.5 shrink-0" />
              )}
              <span className="truncate">{node.name}</span>
            </button>
            <span className="hidden shrink-0 items-center gap-1 group-hover:flex">
              <button
                type="button"
                title={t('ide.moveFolder')}
                onClick={() => {
                  const to = window.prompt(t('ide.moveFolderPrompt'), node.path);
                  if (!to || to === node.path) return;
                  if (!renameFolder(node.path, to)) toast.error(t('ide.moveFailed', { path: to }));
                }}
                className="text-muted-foreground hover:text-foreground"
              >
                <Pencil className="h-3 w-3" />
              </button>
              <button
                type="button"
                title={t('ide.deleteFolder')}
                onClick={() => {
                  const count = countFiles(node);
                  if (!window.confirm(t('ide.deleteFolderConfirm', { path: node.path, count }))) return;
                  deleteFolder(node.path);
                }}
                className="text-muted-foreground hover:text-destructive"
              >
                <Trash2 className="h-3 w-3" />
              </button>
            </span>
          </div>
          {!isCollapsed && node.children.map((c) => renderNode(c, depth + 1))}
        </div>
      );
    }
    const locked = isToolchainFile(node.path);
    const active = node.path === activePath;
    const Icon = locked ? Lock : isPreviewableImage(node.path) ? FileImage : FileCode2;
    return (
      <div
        key={node.path}
        className={cn(
          'group flex items-center gap-1 py-1 pr-2 text-xs',
          active ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
        )}
        style={pad}
        {...(locked ? {} : dragProps(node.path, false))}
      >
        <button
          type="button"
          onClick={() => open(node.path)}
          className="flex min-w-0 flex-1 items-center gap-1.5"
        >
          <Icon className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <span className="truncate">{node.name}</span>
        </button>
        <span className="hidden shrink-0 items-center gap-1 group-hover:flex">
          <button
            type="button"
            title={t('ide.download')}
            onClick={() => downloadFile(node.path, files[node.path] ?? '')}
            className="text-muted-foreground hover:text-foreground"
          >
            <Download className="h-3 w-3" />
          </button>
          {!locked && (
            <>
              <button
                type="button"
                title="Rename"
                onClick={() => {
                  const to = window.prompt('Rename file', node.path);
                  if (to && to !== node.path) renameFile(node.path, to);
                }}
                className="text-muted-foreground hover:text-foreground"
              >
                <Pencil className="h-3 w-3" />
              </button>
              <button
                type="button"
                title="Delete"
                onClick={() => {
                  if (window.confirm(`Delete ${node.path}?`)) deleteFile(node.path);
                }}
                className="text-muted-foreground hover:text-destructive"
              >
                <Trash2 className="h-3 w-3" />
              </button>
            </>
          )}
        </span>
      </div>
    );
  };

  return (
    <div
      className={cn('flex h-full flex-col', dragOver && 'ring-2 ring-inset ring-ring')}
      onDragOver={(e) => {
        // An internal move is not an upload — let the folder rows handle it.
        if (e.dataTransfer.types.includes(DND_PATH)) return;
        e.preventDefault();
        setDragOver(true);
      }}
      onDragLeave={(e) => {
        // Ignore drag-leave bubbling from children.
        if (e.currentTarget.contains(e.relatedTarget as Node)) return;
        setDragOver(false);
      }}
      onDrop={(e) => {
        if (e.dataTransfer.types.includes(DND_PATH)) return;
        e.preventDefault();
        setDragOver(false);
        void uploadFiles(e.dataTransfer.files);
      }}
    >
      <input
        ref={fileInputRef}
        type="file"
        multiple
        hidden
        onChange={(e) => {
          void uploadFiles(e.target.files);
          e.target.value = '';
        }}
      />
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Files</span>
        <span className="flex items-center gap-2">
          <button
            type="button"
            title={t('ide.upload')}
            onClick={() => fileInputRef.current?.click()}
            className="text-muted-foreground hover:text-foreground"
          >
            <Upload className="h-4 w-4" />
          </button>
          <button
            type="button"
            title={t('ide.downloadZip')}
            onClick={() => downloadProjectZip('site-files.zip', files)}
            className="text-muted-foreground hover:text-foreground"
          >
            <FileArchive className="h-4 w-4" />
          </button>
          <button
            type="button"
            title={t('ide.newFile')}
            onClick={() => setAdding((v) => !v)}
            className="text-muted-foreground hover:text-foreground"
          >
            <FilePlus2 className="h-4 w-4" />
          </button>
        </span>
      </div>
      {adding && (
        <div className="border-b px-2 py-1.5">
          {/* eslint-disable-next-line jsx-a11y/no-autofocus */}
          <input
            autoFocus
            value={newName}
            placeholder="src/components/Button.tsx"
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') submitNew();
              if (e.key === 'Escape') {
                setAdding(false);
                setNewName('');
              }
            }}
            onBlur={submitNew}
            className="w-full rounded border bg-background px-2 py-1 text-xs outline-none focus:ring-1 focus:ring-ring"
          />
        </div>
      )}
      {/* The empty space below the tree is the repo root, so a file can be dragged
          back out of a folder. */}
      <div
        className={cn(
          'min-h-0 flex-1 overflow-auto py-1',
          dropTarget === '' && 'bg-primary/10 ring-1 ring-inset ring-primary/50',
        )}
        {...dropProps('')}
      >
        {tree.map((n) => renderNode(n, 0))}
      </div>
    </div>
  );
}
