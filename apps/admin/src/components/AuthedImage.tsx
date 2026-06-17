import { useEffect, useState } from 'react';
import { fetchObjectUrl, mediaContentPath } from '../lib/api';
import { cn } from '../lib/cn';

/**
 * Renders a media asset that lives behind the bearer-protected content endpoint.
 * Fetches the bytes as a blob and shows them via an object URL.
 */
export function AuthedImage({
  id,
  variant,
  className,
  fallback,
}: {
  id: string;
  variant?: string;
  className?: string;
  fallback?: React.ReactNode;
}) {
  const [url, setUrl] = useState<string>();
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    let obj: string | undefined;
    setFailed(false);

    const accept = (u: string) => {
      if (!active) {
        URL.revokeObjectURL(u);
        return true;
      }
      obj = u;
      setUrl(u);
      return true;
    };

    // Prefer the requested variant (e.g. the small WebP thumb); if it isn't
    // available yet, fall back to the original so something still renders.
    fetchObjectUrl(mediaContentPath(id, variant))
      .then(accept)
      .catch(() => {
        if (!active || !variant) {
          if (active) setFailed(true);
          return;
        }
        fetchObjectUrl(mediaContentPath(id))
          .then(accept)
          .catch(() => active && setFailed(true));
      });

    return () => {
      active = false;
      if (obj) URL.revokeObjectURL(obj);
    };
  }, [id, variant]);

  if (failed || !url) return <>{fallback ?? <div className={cn('bg-muted', className)} />}</>;
  return <img src={url} alt="" className={className} />;
}
