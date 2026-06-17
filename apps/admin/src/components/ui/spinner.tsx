import { Loader2 } from 'lucide-react';
import { cn } from '../../lib/cn';

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={cn('h-4 w-4 animate-spin', className)} aria-hidden />;
}

export function CenteredSpinner({ label }: { label?: string }) {
  return (
    <div className="flex h-40 flex-col items-center justify-center gap-2 text-muted-foreground">
      <Spinner className="h-6 w-6" />
      {label ? <span className="text-sm">{label}</span> : null}
    </div>
  );
}
