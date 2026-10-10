import { ExternalLink } from 'lucide-react';
import { cn } from './cn';

/**
 * A site's address, as a link that opens the site in a new tab.
 *
 * <p>Provisioned hostnames are long (`some-site-name-some-workspace.dcms.highgeek.eu`) and have no
 * space to break at, so as plain text they ran off the side of a phone. `overflow-wrap: anywhere`
 * breaks them only when the line is actually too short — and, unlike `break-word`, it also lets a
 * flex item shrink below the hostname's width, which is the case that overflowed.</p>
 *
 * <p>`host` is a bare hostname, or a URL when the caller already has one.</p>
 */
export function HostLink({ host, className }: { host: string; className?: string }) {
  const href = /^https?:\/\//.test(host) ? host : `https://${host}`;
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className={cn(
        'inline min-w-0 text-primary underline-offset-4 [overflow-wrap:anywhere] hover:underline',
        className,
      )}
    >
      {host}
      <ExternalLink className="ml-1 inline h-3.5 w-3.5 align-[-0.125em] opacity-70" aria-hidden />
    </a>
  );
}
