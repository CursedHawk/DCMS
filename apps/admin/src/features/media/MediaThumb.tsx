import { FileAudio, FileVideo, File as FileIcon } from 'lucide-react';
import { AuthedImage } from '../../components/AuthedImage';
import { cn } from '../../lib/cn';
import type { MediaCategory } from './api';

/** Square media preview: image thumbnail (authed) or a type icon. */
export function MediaThumb({
  id,
  category,
  className,
}: {
  id: string;
  category: MediaCategory;
  className?: string;
}) {
  const Icon = category === 'Video' ? FileVideo : category === 'Audio' ? FileAudio : FileIcon;
  const iconBox = (
    <div className={cn('flex h-full w-full items-center justify-center bg-muted text-muted-foreground', className)}>
      <Icon className="h-8 w-8" />
    </div>
  );

  if (category === 'Image') {
    return <AuthedImage id={id} className={cn('h-full w-full object-cover', className)} fallback={iconBox} />;
  }
  return iconBox;
}
