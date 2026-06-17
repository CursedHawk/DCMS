import { FileAudio, FileVideo, File as FileIcon } from 'lucide-react';
import { AuthedImage } from '../../components/AuthedImage';
import { cn } from '../../lib/cn';
import type { MediaCategory, MediaStatus } from './api';

/**
 * Square media preview: image thumbnail (authed) or a type icon. Images render
 * the lightweight 160px WebP `thumb` variant rather than the full original, so
 * grids of many assets stay fast; AuthedImage falls back to the original if the
 * variant isn't there yet. While an image is still processing its variants don't
 * exist, so we show the icon until it's Ready.
 */
export function MediaThumb({
  id,
  category,
  status,
  className,
}: {
  id: string;
  category: MediaCategory;
  status?: MediaStatus;
  className?: string;
}) {
  const Icon = category === 'Video' ? FileVideo : category === 'Audio' ? FileAudio : FileIcon;
  const iconBox = (
    <div className={cn('flex h-full w-full items-center justify-center bg-muted text-muted-foreground', className)}>
      <Icon className="h-8 w-8" />
    </div>
  );

  if (category !== 'Image' || (status && status !== 'Ready')) {
    return iconBox;
  }
  return <AuthedImage id={id} variant="thumb" className={cn('h-full w-full object-cover', className)} fallback={iconBox} />;
}
