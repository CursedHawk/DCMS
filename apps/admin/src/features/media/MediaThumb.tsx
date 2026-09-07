import { FileAudio, FileVideo, File as FileIcon } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { AuthedImage } from '../../components/AuthedImage';
import { cn } from '@dcms/ui';
import type { MediaCategory, MediaStatus } from './api';

/**
 * Square media preview: image thumbnail (authed) or a type icon. Images render
 * the lightweight 160px WebP `thumb` variant rather than the full original, so
 * grids of many assets stay fast; AuthedImage falls back to the original if the
 * variant isn't there yet. While an image is still processing its variants don't
 * exist, so we show the icon until it's Ready.
 *
 * Thumbnails are fetched only once the tile is near the viewport. Media bytes sit
 * behind a bearer-protected endpoint, so they cannot use `<img loading="lazy">` —
 * AuthedImage has to fetch them itself, and mounting a whole library's worth at
 * once fired one request per asset the moment the page opened.
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
  const hostRef = useRef<HTMLDivElement>(null);
  const [near, setNear] = useState(false);

  useEffect(() => {
    if (near) return;
    const host = hostRef.current;
    if (!host) return;
    // No IntersectionObserver (very old browser, jsdom): load eagerly rather
    // than showing an icon forever.
    if (typeof IntersectionObserver === 'undefined') {
      setNear(true);
      return;
    }
    // A generous margin so a thumbnail is already there by the time a normal
    // scroll brings the tile into view.
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setNear(true);
          observer.disconnect();
        }
      },
      { rootMargin: '400px' },
    );
    observer.observe(host);
    return () => observer.disconnect();
  }, [near]);

  const Icon = category === 'Video' ? FileVideo : category === 'Audio' ? FileAudio : FileIcon;
  const iconBox = (
    <div className={cn('flex h-full w-full items-center justify-center bg-muted text-muted-foreground', className)}>
      <Icon className="h-8 w-8" />
    </div>
  );

  const showImage = category === 'Image' && (!status || status === 'Ready') && near;

  // The host is always rendered and always fills the tile, so it has a box for
  // the observer to measure whichever branch is showing.
  return (
    <div ref={hostRef} className="h-full w-full">
      {showImage ? (
        <AuthedImage
          id={id}
          variant="thumb"
          className={cn('h-full w-full object-cover', className)}
          fallback={iconBox}
        />
      ) : (
        iconBox
      )}
    </div>
  );
}
