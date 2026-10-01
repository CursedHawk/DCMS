import { useQueryClient } from '@tanstack/react-query';
import { AlertCircle, CheckCircle2, UploadCloud } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, cn, Progress, toast, toastApiError } from '@dcms/ui';
import { ApiError, api } from '../../lib/api';
import { formatSize } from './api';
import { isInternalDrag } from './dnd';
import {
  DriveCancelled,
  forgetDriveToken,
  importDriveFile,
  pickFromDrive,
  preloadDrive,
  useDriveConfig,
} from './googleDrive';

/** One queued file and how far it has got. */
interface UploadItem {
  id: number;
  name: string;
  size: number;
  source: 'device' | 'drive';
  /** 0–1 of bytes sent; 1 while the server is still processing the request. */
  progress: number;
  status: 'pending' | 'uploading' | 'done' | 'error';
  error?: string;
}

/** A file to add, whichever way its bytes reach the server. */
interface Job {
  name: string;
  size: number;
  source: UploadItem['source'];
  send: (onProgress: (fraction: number) => void) => Promise<{ id: string }>;
}

let nextId = 0;

/**
 * Drag-and-drop / click uploader, plus Google Drive when the platform has it configured.
 * Files are added one at a time to /admin/media, optionally filed into `folderId`, each
 * with its own row in the progress list — a Drive import is just another row.
 *
 * Sequential rather than parallel on purpose: media files are large, and the
 * per-file bars are only meaningful if the browser is not splitting bandwidth
 * between six of them at once.
 */
export function MediaUploader({
  onUploaded,
  folderId,
  multiple = true,
}: {
  onUploaded?: (id: string) => void;
  folderId?: string | null;
  /** False where only one file can be the answer (the single media picker). */
  multiple?: boolean;
}) {
  const { t, i18n } = useTranslation();
  const qc = useQueryClient();
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [items, setItems] = useState<UploadItem[]>([]);
  const [busy, setBusy] = useState(false);
  const drive = useDriveConfig().data;

  const patch = (id: number, changes: Partial<UploadItem>) =>
    setItems((prev) => prev.map((it) => (it.id === id ? { ...it, ...changes } : it)));

  async function run(jobs: Job[]) {
    if (jobs.length === 0) return;

    const queued: UploadItem[] = jobs.map((j) => ({
      id: nextId++,
      name: j.name,
      size: j.size,
      source: j.source,
      progress: 0,
      status: 'pending',
    }));
    // Replace rather than append: the previous batch's rows have been read by
    // now, and keeping them would grow the panel without bound across a session.
    setItems(queued);
    setBusy(true);

    try {
      for (const [index, job] of jobs.entries()) {
        const item = queued[index];
        // A Drive file is downloaded by the server, so there are no bytes to count here:
        // it sits at "the server is processing" until the request returns.
        patch(item.id, { status: 'uploading', progress: job.source === 'drive' ? 1 : 0 });
        try {
          const res = await job.send((fraction) => patch(item.id, { progress: fraction }));
          patch(item.id, { status: 'done', progress: 1 });
          onUploaded?.(res.id);
        } catch (e) {
          // Most likely the token expired mid-batch; the next pick signs in afresh.
          if (job.source === 'drive') forgetDriveToken();
          const message = e instanceof ApiError ? e.message : t('errors.generic');
          patch(item.id, { status: 'error', error: message });
          toastApiError(e, t, job.name);
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

  function upload(files: FileList | File[]) {
    return run(
      Array.from(files).map((file) => ({
        name: file.name,
        size: file.size,
        source: 'device' as const,
        send: (onProgress) => {
          const form = new FormData();
          form.append('file', file);
          if (folderId) form.append('folderId', folderId);
          return api.uploadWithProgress<{ id: string }>('/admin/media', form, onProgress);
        },
      })),
    );
  }

  async function importFromDrive() {
    if (!drive) return;
    try {
      const { token, files } = await pickFromDrive(drive, { multiple, locale: i18n.language });
      await run(
        files.map((f) => ({
          name: f.name,
          size: f.sizeBytes,
          source: 'drive' as const,
          send: () => importDriveFile(token, f.id, folderId),
        })),
      );
    } catch (e) {
      if (!(e instanceof DriveCancelled)) toast.error(t('media.drive.unavailable'));
    }
  }

  const done = items.filter((i) => i.status === 'done').length;

  return (
    <div className="space-y-2">
      {/* The dashed frame is the drop target and holds every way in: the device (the whole
          upper area) and, when configured, Drive beneath it. */}
      <div
        // An asset being dragged between folders is a drop on this same page. Without this the
        // upload zone lights up and then tries to upload a file it does not have — the whole
        // reason the internal drag carries its own MIME type.
        onDragOver={(e) => {
          if (isInternalDrag(e)) return;
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          if (isInternalDrag(e)) return;
          e.preventDefault();
          setDragging(false);
          if (e.dataTransfer.files.length) void upload(multiple ? e.dataTransfer.files : [e.dataTransfer.files[0]]);
        }}
        className={cn(
          'rounded-lg border-2 border-dashed transition-colors',
          dragging ? 'border-primary bg-accent/40' : 'border-border hover:border-primary/50 hover:bg-accent/20',
        )}
      >
        <button
          type="button"
          onClick={() => inputRef.current?.click()}
          className={cn(
            'flex w-full flex-col items-center justify-center gap-2 rounded-lg text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
            drive ? 'pb-4 pt-8' : 'py-10',
          )}
        >
          <UploadCloud className="h-6 w-6 text-muted-foreground" />
          <span className="text-muted-foreground">
            {busy ? t('media.uploadingCount', { done, total: items.length }) : t('media.dropHere')}
          </span>
        </button>
        <input
          ref={inputRef}
          type="file"
          multiple={multiple}
          hidden
          onChange={(e) => {
            if (e.target.files?.length) void upload(e.target.files);
            e.target.value = '';
          }}
        />
        {drive ? (
          <div className="flex justify-center pb-5">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={busy}
              // Google's scripts arrive before the click, so the sign-in popup opens inside
              // the click's activation window instead of being blocked.
              onPointerEnter={() => void preloadDrive().catch(() => undefined)}
              onFocus={() => void preloadDrive().catch(() => undefined)}
              onClick={() => void importFromDrive()}
            >
              <DriveGlyph className="h-4 w-4" />
              {t('media.drive.import')}
            </Button>
          </div>
        ) : null}
      </div>

      {items.length > 0 ? (
        <ul className="divide-y rounded-lg border">
          {items.map((it) => (
            <li key={it.id} className="space-y-1.5 p-2.5">
              <div className="flex items-center gap-2 text-xs">
                {it.status === 'done' ? (
                  <CheckCircle2 className="h-3.5 w-3.5 shrink-0 text-[hsl(var(--success))]" />
                ) : it.status === 'error' ? (
                  <AlertCircle className="h-3.5 w-3.5 shrink-0 text-destructive" />
                ) : it.source === 'drive' ? (
                  <DriveGlyph className="h-3.5 w-3.5 shrink-0" />
                ) : null}
                <span className="min-w-0 flex-1 truncate font-medium" title={it.name}>
                  {it.name}
                </span>
                {it.size > 0 ? (
                  <span className="shrink-0 tabular-nums text-muted-foreground">{formatSize(it.size)}</span>
                ) : null}
                {it.status === 'uploading' ? (
                  <span className="shrink-0 text-right tabular-nums text-muted-foreground">
                    {it.source === 'drive' ? t('media.drive.importing') : `${Math.round(it.progress * 100)}%`}
                  </span>
                ) : null}
              </div>
              {it.status === 'error' ? (
                <p className="text-xs text-destructive">{it.error}</p>
              ) : (
                <Progress
                  value={it.progress}
                  label={it.name}
                  className={cn(
                    it.source === 'drive' && it.status === 'uploading' && 'animate-pulse motion-reduce:animate-none',
                  )}
                />
              )}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

/** Google's Drive mark, unaltered as their brand rules require. Decorative: the label says Drive. */
function DriveGlyph({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 87.3 78" className={className} aria-hidden="true">
      <path d="m6.6 66.85 3.85 6.65c.8 1.4 1.95 2.5 3.3 3.3l13.75-23.8h-27.5c0 1.55.4 3.1 1.2 4.5z" fill="#0066da" />
      <path d="m43.65 25-13.75-23.8c-1.35.8-2.5 1.9-3.3 3.3l-25.4 44a9.06 9.06 0 0 0 -1.2 4.5h27.5z" fill="#00ac47" />
      <path d="m73.55 76.8c1.35-.8 2.5-1.9 3.3-3.3l1.6-2.75 7.65-13.25c.8-1.4 1.2-2.95 1.2-4.5h-27.502l5.852 11.5z" fill="#ea4335" />
      <path d="m43.65 25 13.75-23.8c-1.35-.8-2.9-1.2-4.5-1.2h-18.5c-1.6 0-3.15.45-4.5 1.2z" fill="#00832d" />
      <path d="m59.8 53h-32.3l-13.75 23.8c1.35.8 2.9 1.2 4.5 1.2h50.8c1.6 0 3.15-.45 4.5-1.2z" fill="#2684fc" />
      <path d="m73.4 26.5-12.7-22c-.8-1.4-1.95-2.5-3.3-3.3l-13.75 23.8 16.15 28h27.45c0-1.55-.4-3.1-1.2-4.5z" fill="#ffba00" />
    </svg>
  );
}
