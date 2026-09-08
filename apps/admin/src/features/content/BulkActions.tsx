import { CalendarX, CheckCircle2, EyeOff, Trash2, X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, PermissionTooltip, useCan } from '@dcms/ui';
import { Perm } from '../../lib/permissions';
import { type BulkVerb, useBulkContentAction } from './api';

/**
 * What you can do to a selection of content items.
 *
 * <p>Every action here already existed one item at a time, inside the editor. Doing it to
 * twelve items meant opening twelve editors — which is why a list that can select rows and
 * then offers nothing to do with them is only half a feature.</p>
 *
 * <p><b>Partial success is reported as partial success.</b> There is no bulk endpoint behind
 * this and deliberately so (see `useBulkContentAction`), so eight publishes are eight requests
 * and one of them can be refused while the others succeed. The toast then says seven and one,
 * rather than a green tick that is three-quarters true.</p>
 *
 * <p>Controls the caller cannot use are disabled and say why, rather than vanishing: someone
 * who can write content but not publish it should learn the name of the permission to ask for,
 * not wonder where the button went.</p>
 */
export function BulkActions({
  ids,
  onDone,
  onClear,
}: {
  ids: readonly string[];
  /** Called after every action, so the list can drop a selection that no longer exists. */
  onDone: () => void;
  onClear: () => void;
}) {
  const { t } = useTranslation();
  const bulk = useBulkContentAction();
  const canPublish = useCan(Perm.ContentPublish);
  const canWrite = useCan(Perm.ContentWrite);

  const run = (verb: BulkVerb) => {
    if (verb === 'delete' && !window.confirm(t('content.bulk.deleteConfirm', { count: ids.length }))) {
      return;
    }

    bulk.mutate(
      { verb, ids: [...ids] },
      {
        onSuccess: (result) => {
          if (result.failed.length === 0) {
            toast.success(t(`content.bulk.done.${verb}`, { count: result.done.length }));
          } else if (result.done.length === 0) {
            toast.error(t('content.bulk.allFailed', { count: result.failed.length }));
          } else {
            toast.warning(
              t('content.bulk.partial', {
                done: result.done.length,
                failed: result.failed.length,
              }),
            );
          }
          onDone();
        },
      },
    );
  };

  return (
    <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-accent/40 px-3 py-2">
      <span className="text-sm font-medium">{t('content.bulk.selected', { count: ids.length })}</span>

      <div className="ml-auto flex flex-wrap items-center gap-2">
        <PermissionTooltip perm={Perm.ContentPublish} reason={t('content.bulk.needsPublish')}>
          <Button size="sm" variant="outline" disabled={!canPublish || bulk.isPending} onClick={() => run('publish')}>
            <CheckCircle2 className="h-4 w-4" /> {t('content.bulk.publish')}
          </Button>
        </PermissionTooltip>

        <PermissionTooltip perm={Perm.ContentPublish} reason={t('content.bulk.needsPublish')}>
          <Button size="sm" variant="outline" disabled={!canPublish || bulk.isPending} onClick={() => run('unpublish')}>
            <EyeOff className="h-4 w-4" /> {t('content.bulk.unpublish')}
          </Button>
        </PermissionTooltip>

        <PermissionTooltip perm={Perm.ContentPublish} reason={t('content.bulk.needsPublish')}>
          <Button
            size="sm"
            variant="outline"
            disabled={!canPublish || bulk.isPending}
            onClick={() => run('cancelSchedule')}
          >
            <CalendarX className="h-4 w-4" /> {t('content.bulk.cancelSchedule')}
          </Button>
        </PermissionTooltip>

        <PermissionTooltip perm={Perm.ContentWrite} reason={t('content.bulk.needsWrite')}>
          <Button size="sm" variant="destructive" disabled={!canWrite || bulk.isPending} onClick={() => run('delete')}>
            <Trash2 className="h-4 w-4" /> {t('common.delete')}
          </Button>
        </PermissionTooltip>

        <Button size="sm" variant="ghost" aria-label={t('common.clearSelection')} onClick={onClear}>
          <X className="h-4 w-4" />
        </Button>
      </div>
    </div>
  );
}
