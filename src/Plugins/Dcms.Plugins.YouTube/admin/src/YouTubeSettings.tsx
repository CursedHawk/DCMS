import { useMutation } from '@tanstack/react-query';
import { RefreshCw } from 'lucide-react';
import { Button, cn, toast, toastApiError } from '@dcms/ui';
import {
  instancePath,
  SchemaSettings,
  usePluginApi,
  usePluginHost,
  usePluginT,
  type PluginScreenProps,
} from '@dcms/plugin-ui';

/**
 * The settings form with Sync now above it. The timer only syncs when the channel has something
 * new; Sync now always runs a full sync, which also refreshes titles and view counts.
 */
export function YouTubeSettings(props: PluginScreenProps) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const canSync = usePluginHost().can('plugin:youtube:sync');
  const slug = props.instance?.slug;

  const sync = useMutation({
    mutationFn: () => api.post<{ created: number; updated: number; removed: number }>(instancePath(slug!, '/sync')),
    onSuccess: (r) => toast.success(t('youtube.syncDone', r)),
    onError: (e) => toastApiError(e, t, t('youtube.syncFailed')),
  });

  return (
    <div className="space-y-4">
      {slug && canSync && (
        <div className="flex justify-end">
          <Button type="button" variant="outline" size="sm" disabled={sync.isPending} onClick={() => sync.mutate()}>
            <RefreshCw className={cn('size-4', sync.isPending && 'animate-spin')} />
            {t('youtube.syncNow')}
          </Button>
        </div>
      )}
      <SchemaSettings {...props} />
    </div>
  );
}
