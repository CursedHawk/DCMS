import { useQueryClient } from '@tanstack/react-query';
import { UploadCloud } from 'lucide-react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Spinner } from '../../components/ui/spinner';
import { cn } from '../../lib/cn';
import { ApiError, api } from '../../lib/api';

/** Drag-and-drop / click uploader. Uploads files one by one to /admin/media. */
export function MediaUploader({ onUploaded }: { onUploaded?: (id: string) => void }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [busy, setBusy] = useState(false);

  async function upload(files: FileList | File[]) {
    setBusy(true);
    try {
      for (const file of Array.from(files)) {
        const form = new FormData();
        form.append('file', file);
        try {
          const res = await api.upload<{ id: string }>('/admin/media', form);
          onUploaded?.(res.id);
        } catch (e) {
          toast.error(e instanceof ApiError ? `${file.name}: ${e.message}` : t('errors.generic'));
        }
      }
      await qc.invalidateQueries({ queryKey: ['media'] });
    } finally {
      setBusy(false);
    }
  }

  return (
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
      {busy ? <Spinner className="h-6 w-6" /> : <UploadCloud className="h-6 w-6 text-muted-foreground" />}
      <span className="text-muted-foreground">{t('media.dropHere')}</span>
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
  );
}
