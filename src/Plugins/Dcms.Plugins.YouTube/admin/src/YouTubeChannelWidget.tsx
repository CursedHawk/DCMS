import { useEffect, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { AlertTriangle, RefreshCw } from 'lucide-react';
import { Button, Input, cn, toast, toastApiError } from '@dcms/ui';
import { instancePath, usePluginApi, usePluginT, type PluginWidgetProps } from '@dcms/plugin-ui';

interface Resolved {
  channel: { id: string; title: string; handle?: string | null; avatarUrl?: string | null } | null;
  error: string | null;
}

/**
 * RJSF widget for the `"format": "youtube-channel"` field. A channel link is easy to get
 * subtly wrong (a video link, someone else's channel), so the widget shows which channel it
 * resolves to before the admin saves — and offers Sync now for the saved settings.
 */
export function YouTubeChannelWidget({ id, value, onChange, disabled, readonly, registry }: PluginWidgetProps) {
  const { t } = usePluginT();
  const api = usePluginApi();
  // Absent while an instance is being created: there is nothing to look up through yet.
  const slug = registry?.formContext?.instanceSlug;
  const text = typeof value === 'string' ? value : '';

  // Look the link up once typing pauses, not on every keystroke: each lookup spends quota.
  const [settled, setSettled] = useState(text.trim());
  useEffect(() => {
    const timer = setTimeout(() => setSettled(text.trim()), 600);
    return () => clearTimeout(timer);
  }, [text]);

  const resolve = useQuery({
    queryKey: ['youtube', 'resolve', slug, settled],
    queryFn: () => api.get<Resolved>(instancePath(slug!, `/resolve?channel=${encodeURIComponent(settled)}`)),
    enabled: !!slug && settled.length > 0,
    staleTime: 5 * 60_000,
    retry: false,
  });

  const sync = useMutation({
    mutationFn: () => api.post<{ created: number; updated: number; removed: number }>(instancePath(slug!, '/sync')),
    onSuccess: (r) => toast.success(t('youtube.syncDone', r)),
    onError: (e) => toastApiError(e, t, t('youtube.syncFailed')),
  });

  const notConfigured = (resolve.error as { status?: number } | null)?.status === 501;
  const channel = resolve.data?.channel;

  return (
    <div className="space-y-2">
      <Input
        id={id}
        value={text}
        disabled={disabled || readonly}
        placeholder={t('youtube.placeholder')}
        onChange={(e) => onChange(e.target.value === '' ? undefined : e.target.value)}
      />

      {resolve.isFetching && <p className="text-xs text-muted-foreground">{t('youtube.checking')}</p>}

      {notConfigured && (
        <p className="flex items-center gap-1 text-xs text-muted-foreground">
          <AlertTriangle className="size-3 shrink-0" />
          {t('youtube.notConfigured')}
        </p>
      )}
      {resolve.isError && !notConfigured && <p className="text-xs text-destructive">{t('youtube.lookupFailed')}</p>}
      {resolve.data?.error && <p className="text-xs text-destructive">{resolve.data.error}</p>}

      {channel && (
        <div className="flex items-center gap-3 rounded-md border p-3">
          {channel.avatarUrl && <img src={channel.avatarUrl} alt="" className="size-8 rounded-full object-cover" />}
          <span className="min-w-0 flex-1">
            <span className="block truncate font-medium">{channel.title}</span>
            {channel.handle && <span className="block truncate text-xs text-muted-foreground">{channel.handle}</span>}
          </span>
          <Button type="button" variant="ghost" size="sm" disabled={sync.isPending} onClick={() => sync.mutate()}>
            <RefreshCw className={cn('size-4', sync.isPending && 'animate-spin')} />
            {t('youtube.syncNow')}
          </Button>
        </div>
      )}
    </div>
  );
}
