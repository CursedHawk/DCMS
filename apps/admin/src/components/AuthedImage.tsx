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
    fetchObjectUrl(mediaContentPath(id, variant))
      .then((u) => {
        if (active) {
          obj = u;
          setUrl(u);
        } else {
          URL.revokeObjectURL(u);
        }
      })
      .catch(() => active && setFailed(true));
    return () => {
      active = false;
      if (obj) URL.revokeObjectURL(obj);
    };
  }, [id, variant]);

  if (failed || !url) return <>{fallback ?? <div className={cn('bg-muted', className)} />}</>;
  return <img src={url} alt="" className={className} />;
}
