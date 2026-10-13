import { AlertTriangle } from 'lucide-react';
import { Button } from '../ui/button';
import { EmptyState } from '../ui/empty-state';

/**
 * What a list shows when its request failed — never its empty state.
 *
 * <p>A list that could not load and a list with nothing in it look identical unless one of
 * them says so, and "there is nothing here" is the more damaging of the two to get wrong: it
 * reads as data loss. Pass it as `DataTable`'s `error`.</p>
 */
export function LoadFailed({
  title,
  retryLabel,
  onRetry,
}: {
  title: string;
  retryLabel: string;
  onRetry: () => void;
}) {
  return (
    <EmptyState
      icon={AlertTriangle}
      title={title}
      action={
        <Button variant="outline" size="sm" onClick={onRetry}>
          {retryLabel}
        </Button>
      }
    />
  );
}
