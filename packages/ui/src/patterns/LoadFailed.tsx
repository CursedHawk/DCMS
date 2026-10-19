import { AlertTriangle } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../ui/button';
import { EmptyState } from '../ui/empty-state';

/**
 * What a list shows when its request failed — never its empty state.
 *
 * <p>A list that could not load and a list with nothing in it look identical unless one of
 * them says so, and "there is nothing here" is the more damaging of the two to get wrong: it
 * reads as data loss. Pass it as `DataTable`'s `error`, or render it where the list would be.</p>
 *
 * <p>Both labels default to the apps' `common.loadFailed` / `common.retry`, because every page
 * says the same thing here and every call site used to spell both keys out.</p>
 */
export function LoadFailed({
  title,
  retryLabel,
  onRetry,
}: {
  title?: string;
  retryLabel?: string;
  onRetry: () => void;
}) {
  const { t } = useTranslation();
  return (
    <EmptyState
      icon={AlertTriangle}
      title={title ?? t('common.loadFailed', { defaultValue: 'This could not be loaded' })}
      action={
        <Button variant="outline" size="sm" onClick={onRetry}>
          {retryLabel ?? t('common.retry', { defaultValue: 'Try again' })}
        </Button>
      }
    />
  );
}
