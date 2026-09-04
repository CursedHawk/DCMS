import { ArrowLeft, ArrowRight, ImagePlus, Layers, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Button,
  CenteredSpinner,
  cn,
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@dcms/admin-ui';
import { MediaThumb } from './MediaThumb';
import { MediaUploader } from './MediaUploader';
import { ROOT_FOLDER, type MediaCategory, useMedia, useMediaFolders } from './api';

/**
 * Form field that picks an *ordered* list of media asset ids from the library
 * (e.g. a gallery's images). Selection is toggled in a dialog; whole folders can
 * be added at once ("point this at a folder"). Bound value is a string[] of ids.
 */
export function MediaMultiPicker({
  value,
  onChange,
  category = 'Image',
}: {
  value?: string[];
  onChange: (ids: string[]) => void;
  category?: MediaCategory;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [folder, setFolder] = useState<string>('all');
  const media = useMedia(folder === 'all' ? undefined : folder);
  const folders = useMediaFolders();

  const ids = value ?? [];
  const byId = new Map((media.data ?? []).map((a) => [a.id, a]));
  const items = (media.data ?? []).filter((a) => !category || a.category === category);

  const toggle = (id: string) =>
    onChange(ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id]);
  const remove = (id: string) => onChange(ids.filter((x) => x !== id));
  const move = (i: number, dir: -1 | 1) => {
    const j = i + dir;
    if (j < 0 || j >= ids.length) return;
    const next = ids.slice();
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  };

  // "Serve a whole folder": append every not-yet-selected asset in the current view.
  const addAllInView = () => {
    const toAdd = items.map((a) => a.id).filter((id) => !ids.includes(id));
    if (toAdd.length) onChange([...ids, ...toAdd]);
  };

  const inView = folder === 'all' ? null : folder;

  return (
    <div className="space-y-2">
      {ids.length > 0 ? (
        <div className="flex flex-wrap gap-2">
          {ids.map((id, i) => {
            const a = byId.get(id);
            return (
              <div key={id} className="group relative h-20 w-20 overflow-hidden rounded-md border bg-muted">
                <MediaThumb id={id} category={a?.category ?? 'Image'} status={a?.status} />
                <button
                  type="button"
                  aria-label="remove"
                  onClick={() => remove(id)}
                  className="absolute right-0 top-0 rounded-bl bg-background/80 p-0.5 hover:bg-background"
                >
                  <X className="h-3 w-3" />
                </button>
                <div className="absolute inset-x-0 bottom-0 flex justify-between bg-background/70 opacity-0 transition group-hover:opacity-100">
                  <button
                    type="button"
                    aria-label="move left"
                    onClick={() => move(i, -1)}
                    disabled={i === 0}
                    className="p-0.5 disabled:opacity-30"
                  >
                    <ArrowLeft className="h-3 w-3" />
                  </button>
                  <button
                    type="button"
                    aria-label="move right"
                    onClick={() => move(i, 1)}
                    disabled={i === ids.length - 1}
                    className="p-0.5 disabled:opacity-30"
                  >
                    <ArrowRight className="h-3 w-3" />
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      ) : (
        <p className="text-xs text-muted-foreground">{t('media.noImages')}</p>
      )}

      <Button type="button" variant="outline" size="sm" onClick={() => setOpen(true)}>
        <ImagePlus className="h-4 w-4" />
        {ids.length > 0 ? t('media.imagesCount', { count: ids.length }) : t('media.addImages')}
      </Button>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent wide>
          <DialogHeader>
            <DialogTitle>{t('media.addImages')}</DialogTitle>
          </DialogHeader>

          <div className="flex flex-wrap items-center gap-2">
            <Select value={folder} onValueChange={setFolder}>
              <SelectTrigger className="h-8 w-48">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">{t('media.folders.all')}</SelectItem>
                <SelectItem value={ROOT_FOLDER}>{t('media.folders.unfiled')}</SelectItem>
                {(folders.data ?? []).map((f) => (
                  <SelectItem key={f.id} value={f.id}>
                    {f.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button type="button" size="sm" variant="secondary" onClick={addAllInView} disabled={items.length === 0}>
              <Layers className="h-4 w-4" />
              {inView ? t('media.addFolder') : t('media.addAll')}
            </Button>
          </div>

          <div className="mb-1">
            <MediaUploader
              folderId={folder !== 'all' && folder !== ROOT_FOLDER ? folder : null}
              onUploaded={(id) => onChange([...ids, id])}
            />
          </div>
          {media.isLoading ? (
            <CenteredSpinner />
          ) : (
            <div className="grid max-h-[50vh] grid-cols-3 gap-3 overflow-y-auto sm:grid-cols-4">
              {items.map((a) => {
                const order = ids.indexOf(a.id);
                const selected = order >= 0;
                return (
                  <button
                    key={a.id}
                    type="button"
                    onClick={() => toggle(a.id)}
                    className={cn(
                      'relative aspect-square overflow-hidden rounded-md border transition-all hover:ring-2 hover:ring-ring',
                      selected && 'ring-2 ring-primary',
                    )}
                  >
                    <MediaThumb id={a.id} category={a.category} status={a.status} />
                    {selected ? (
                      <span className="absolute right-1 top-1 flex h-5 w-5 items-center justify-center rounded-full bg-primary text-[10px] font-bold text-primary-foreground">
                        {order + 1}
                      </span>
                    ) : null}
                  </button>
                );
              })}
            </div>
          )}
          <DialogFooter>
            <Button type="button" onClick={() => setOpen(false)}>
              {t('media.done')}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
