import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { RadioTower } from 'lucide-react';
import { Button, Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, toast, toastApiError } from '@dcms/ui';
import { api } from '../../lib/api';
import { CodeEditor } from '../../pluginHost/CodeEditor';
import { usePluginInstances, type PluginInstance } from '../plugins/api';


/**
 * The workspace's realtime channels (ADR 0037), edited as the `channels.json` the dcms CLI writes:
 * the same document, checked by Monaco against the same schema the server enforces. A save is the
 * channels' settings, so the site's generated client refreshes the way it does after any plugin
 * change.
 */
export function ChannelsView() {
  const { t } = useTranslation();
  const instances = usePluginInstances();
  const [open, setOpen] = useState<PluginInstance | null>(null);
  const sockets = (instances.data ?? []).filter((i) => i.pluginId === 'websockets' && i.enabled);

  return (
    <div className="flex h-full flex-col">
      <div className="border-b px-3 py-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {t('ide.channels.title')}
      </div>
      <div className="flex-1 space-y-1 overflow-y-auto p-2">
        {sockets.length === 0 && <p className="px-1 text-xs text-muted-foreground">{t('ide.channels.none')}</p>}
        {sockets.map((instance) => (
          <button
            key={instance.id}
            type="button"
            className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent"
            onClick={() => setOpen(instance)}
          >
            <RadioTower className="size-4 shrink-0" />
            <span className="truncate">{instance.name}</span>
            <span className="ml-auto font-mono text-xs text-muted-foreground">{instance.slug}</span>
          </button>
        ))}
      </div>
      {open && <ChannelsDialog instance={open} onClose={() => setOpen(null)} />}
    </div>
  );
}

function ChannelsDialog({ instance, onClose }: { instance: PluginInstance; onClose: () => void }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const path = `/admin/plugins/${encodeURIComponent(instance.slug)}/channels`;
  const file = useQuery({ queryKey: ['websockets', instance.slug, 'channels'], queryFn: () => api.get<{ channels: unknown[] }>(path) });
  const schema = useQuery({
    queryKey: ['websockets', instance.slug, 'channels-schema'],
    queryFn: () => api.get<Record<string, unknown>>(`${path}/schema`),
    staleTime: Infinity,
  });
  const [text, setText] = useState<string | null>(null);
  const shown = text ?? (file.data ? JSON.stringify(file.data, null, 2) : '');

  const save = useMutation({
    mutationFn: () => api.put(path, JSON.parse(shown)),
    onSuccess: async () => {
      toast.success(t('common.saved'));
      await qc.invalidateQueries({ queryKey: ['websockets', instance.slug] });
      await qc.invalidateQueries({ queryKey: ['plugin-instances'] });
      onClose();
    },
    onError: (e) => toastApiError(e, t),
  });

  let parses = true;
  try {
    JSON.parse(shown);
  } catch {
    parses = false;
  }

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>{t('ide.channels.editTitle', { name: instance.name })}</DialogTitle>
          <DialogDescription>{t('ide.channels.editHint')}</DialogDescription>
        </DialogHeader>
        {file.data && schema.data && (
          <CodeEditor
            uri={`file:///dcms/websockets/${instance.slug}/channels.json`}
            language="json"
            value={shown}
            onChange={setText}
            height={420}
            jsonSchema={schema.data}
            ariaLabel="channels.json"
          />
        )}
        <div className="flex justify-end gap-2">
          <Button variant="outline" onClick={onClose}>
            {t('actions.cancel')}
          </Button>
          <Button disabled={!parses || save.isPending || !file.data} onClick={() => save.mutate()}>
            {t('actions.save')}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
