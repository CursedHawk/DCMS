import { useQueryClient } from '@tanstack/react-query';
import { AlertCircle, CheckCircle2, UploadCloud } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Progress } from '../../components/ui/progress';
import { cn } from '../../lib/cn';
import { ApiError, api } from '../../lib/api';
import { formatSize } from './api';

/** One queued file and how far its upload has got. */
interface UploadItem {
  id: number;
  name: string;
  size: number;
  /** 0–1 of bytes sent; 1 while the server is still processing the request. */
  progress: number;
  status: 'pending' | 'uploading' | 'done' | 'error';
  error?: string;
}

let nextId = 0;

/**
 * Drag-and-drop / click uploader. Uploads files one at a time to /admin/media,
 * optionally filing them into `folderId`, showing a per-file progress bar.
 *
 * Sequential rather than parallel on purpose: media files are large, and the
 * per-file bars are only meaningful if the browser is not splitting bandwidth
 * between six of them at once.
 */
export function MediaUploader({
  onUploaded,
  folderId,
}: {
  onUploaded?: (id: string) => void;
  folderId?: string | null;
}) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [items, setItems] = useState<UploadItem[]>([]);
  const [busy, setBusy] = useState(false);

  const patch = (id: number, changes: Partial<UploadItem>) =>
    setItems((prev) => prev.map((it) => (it.id === id ? { ...it, ...changes } : it)));

  async function upload(files: FileList | File[]) {
    const list = Array.from(files);
    if (list.length === 0) return;

    const queued: UploadItem[] = list.map((f) => ({
      id: nextId++,
      name: f.name,
      size: f.size,
      progress: 0,
      status: 'pending',
    }));
    // Replace rather than append: the previous batch's rows have been read by
    // now, and keeping them would grow the panel without bound across a session.
    setItems(queued);
    setBusy(true);

    try {
      for (const [index, file] of list.entries()) {
        const item = queued[index];
        patch(item.id, { status: 'uploading' });
        const form = new FormData();
        form.append('file', file);
        if (folderId) form.append('folderId', folderId);
        try {
          const res = await api.uploadWithProgress<{ id: string }>('/admin/media', form, (fraction) =>
            patch(item.id, { progress: fraction }),
          );
          patch(item.id, { status: 'done', progress: 1 });
          onUploaded?.(res.id);
        } catch (e) {
          const message = e instanceof ApiError ? e.message : t('errors.generic');
          patch(item.id, { status: 'error', error: message });
          toast.error(`${file.name}: ${message}`);
        }
      }
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['media'] }),
        qc.invalidateQueries({ queryKey: ['media-folders'] }),
        qc.invalidateQueries({ queryKey: ['media-usage'] }),
      ]);
    } finally {
      setBusy(false);
      // Clear the finished rows, but keep failures on screen — they are the only
      // record of what went wrong once the toast has gone.
      setItems((prev) => prev.filter((it) => it.status === 'error'));
    }
  }

  const done = items.filter((i) => i.status === 'done').length;

  return (
    <div className="space-y-2">
      <button
        type="button"
        onClick={() => inputRef.current?.click()}
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          if (e.dataTransfer.files.length) void upload(e.dataTransfer.files);
        }}
        className={cn(
          'flex w-full flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed py-10 text-sm transition-colors',
          dragging ? 'border-primary bg-accent/40' : 'border-border hover:border-primary/50 hover:bg-accent/20',
        )}
      >
        <UploadCloud className="h-6 w-6 text-muted-foreground" />
        <span className="text-muted-foreground">
          {busy ? t('media.uploadingCount', { done, total: items.length }) : t('media.dropHere')}
        </span>
        <input
          ref={inputRef}
          type="file"
          multiple
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void upload(e.target.files);
            e.target.value = '';
          }}
        />
      </button>

      {items.length > 0 ? (
        <ul className="divide-y rounded-lg border">
          {items.map((it) => (
            <li key={it.id} className="space-y-1.5 p-2.5">
              <div className="flex items-center gap-2 text-xs">
                {it.status === 'done' ? (
                  <CheckCircle2 className="h-3.5 w-3.5 shrink-0 text-[hsl(var(--success))]" />
                ) : it.status === 'error' ? (
                  <AlertCircle className="h-3.5 w-3.5 shrink-0 text-destructive" />
                ) : null}
                <span className="min-w-0 flex-1 truncate font-medium" title={it.name}>
                  {it.name}
                </span>
                <span className="shrink-0 tabular-nums text-muted-foreground">{formatSize(it.size)}</span>
                {it.status === 'uploading' ? (
                  <span className="w-10 shrink-0 text-right tabular-nums text-muted-foreground">
                    {Math.round(it.progress * 100)}%
                  </span>
                ) : null}
              </div>
              {it.status === 'error' ? (
                <p className="text-xs text-destructive">{it.error}</p>
              ) : (
                <Progress value={it.progress} label={it.name} />
              )}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
