import { ImagePlus, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '../../components/ui/dialog';
import { CenteredSpinner } from '../../components/ui/spinner';
import { cn } from '../../lib/cn';
import { MediaThumb } from './MediaThumb';
import { MediaUploader } from './MediaUploader';
import { type MediaCategory, useMedia } from './api';

/** Form field that picks a media asset id from the library (with inline upload). */
export function MediaPicker({
  value,
  onChange,
  category,
}: {
  value?: string;
  onChange: (id: string | undefined) => void;
  category?: MediaCategory;
}) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const media = useMedia();
  const items = (media.data ?? []).filter((a) => !category || a.category === category);

  return (
    <div className="flex items-center gap-3">
      {value ? (
        <div className="relative h-16 w-16 overflow-hidden rounded-md border">
          <MediaThumb id={value} category={category ?? 'Image'} />
          <button
            type="button"
            onClick={() => onChange(undefined)}
            className="absolute right-0 top-0 rounded-bl bg-background/80 p-0.5"
          >
            <X className="h-3 w-3" />
          </button>
        </div>
      ) : null}
      <Button type="button" variant="outline" size="sm" onClick={() => setOpen(true)}>
        <ImagePlus className="h-4 w-4" /> {t('media.pick')}
      </Button>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent wide>
          <DialogHeader>
            <DialogTitle>{t('media.pick')}</DialogTitle>
          </DialogHeader>
          <div className="mb-3">
            <MediaUploader onUploaded={(id) => onChange(id)} />
          </div>
          {media.isLoading ? (
            <CenteredSpinner />
          ) : (
            <div className="grid max-h-[50vh] grid-cols-3 gap-3 overflow-y-auto sm:grid-cols-4">
              {items.map((a) => (
                <button
                  key={a.id}
                  type="button"
                  onClick={() => {
                    onChange(a.id);
                    setOpen(false);
                  }}
                  className={cn(
                    'aspect-square overflow-hidden rounded-md border transition-all hover:ring-2 hover:ring-ring',
                    value === a.id && 'ring-2 ring-primary',
                  )}
                >
                  <MediaThumb id={a.id} category={a.category} status={a.status} />
                </button>
              ))}
            </div>
          )}
        </DialogContent>
      </Dialog>
    </div>
  );
}
