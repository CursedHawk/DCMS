import { useQuery } from '@tanstack/react-query';
import { Plus, RadioTower, Trash2 } from 'lucide-react';
import {
  Button,
  Card,
  CardContent,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
} from '@dcms/ui';
import { instancePath, usePluginApi, usePluginT, type PluginWidgetProps } from '@dcms/plugin-ui';
import { PayloadEditor } from './PayloadEditor';
import { channelProblems, type Channel } from './schema';


/**
 * The instance's channels (`"format": "websocket-channels"`): who may subscribe and publish, and
 * each payload's type. Part of the settings form, so the form's Save saves them and the server
 * checks them against the same schema the IDE and the CLI use.
 */
export function ChannelsWidget({ value, onChange, registry }: PluginWidgetProps) {
  const { t } = usePluginT();
  const api = usePluginApi();
  const slug = registry?.formContext?.instanceSlug;
  const channels = (Array.isArray(value) ? value : []) as Channel[];
  // The payload subset, for the JSON tab's validation and completion.
  const fileSchema = useQuery({
    queryKey: ['websockets', slug, 'channels-schema'],
    queryFn: () => api.get<{ $defs?: Record<string, unknown> }>(instancePath(slug!, '/channels/schema')),
    enabled: !!slug,
    staleTime: Infinity,
  });
  const defs = fileSchema.data?.$defs;
  const metaSchema = defs ? { ...(defs.payload as Record<string, unknown>), $defs: defs } : undefined;

  const set = (index: number, channel: Channel) => onChange(channels.map((c, i) => (i === index ? channel : c)));
  const problems = channelProblems(channels);

  return (
    <div className="space-y-3">
      {channels.length === 0 && (
        <div className="flex items-center gap-2 rounded-md border border-dashed p-4 text-sm text-muted-foreground">
          <RadioTower className="size-4" />
          {t('websockets.empty')}
        </div>
      )}
      {channels.map((channel, index) => (
        <Card key={index}>
          <CardContent className="space-y-3 pt-4">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-[1fr_auto]">
              <div className="space-y-1.5">
                <Label htmlFor={`ws-${index}-name`}>{t('websockets.name')}</Label>
                <Input
                  id={`ws-${index}-name`}
                  className="font-mono"
                  value={channel.name}
                  onChange={(e) => set(index, { ...channel, name: e.target.value })}
                />
              </div>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="self-end"
                aria-label={t('websockets.removeChannel')}
                onClick={() => onChange(channels.filter((_, i) => i !== index))}
              >
                <Trash2 className="size-4" />
              </Button>
            </div>
            <Input
              aria-label={t('websockets.description')}
              placeholder={t('websockets.description')}
              value={channel.description ?? ''}
              onChange={(e) => set(index, { ...channel, description: e.target.value || undefined })}
            />
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <Access
                label={t('websockets.subscribe')}
                value={channel.subscribe ?? 'public'}
                options={['public', 'signedIn', 'permission', 'self']}
                onChange={(subscribe) => set(index, { ...channel, subscribe: subscribe as Channel['subscribe'] })}
              />
              <Access
                label={t('websockets.publish')}
                value={channel.publish ?? 'none'}
                options={['none', 'subscribers', 'permission']}
                onChange={(publish) => set(index, { ...channel, publish: publish as Channel['publish'] })}
              />
            </div>
            <div className="flex flex-wrap gap-6">
              <Toggle id={`ws-${index}-keyed`} label={t('websockets.keyed')} checked={!!channel.keyed}
                onChange={(keyed) => set(index, { ...channel, keyed })} />
              <Toggle id={`ws-${index}-event`} label={t('websockets.raiseEvent')} checked={!!channel.raiseEvent}
                onChange={(raiseEvent) => set(index, { ...channel, raiseEvent })} />
            </div>
            {(channel.subscribe === 'permission' || channel.publish === 'permission') && slug && (
              <p className="text-xs text-muted-foreground">
                {t('websockets.permissionHint', { key: `websockets:${slug}:channel:${channel.name}:…` })}
              </p>
            )}
            <PayloadEditor
              uri={`file:///dcms/websockets/${slug ?? 'new'}/${index}`}
              value={channel.schema}
              metaSchema={metaSchema}
              onChange={(schema) => set(index, { ...channel, schema })}
            />
          </CardContent>
        </Card>
      ))}
      {problems.length > 0 && (
        <ul className="list-disc pl-5 text-xs text-destructive">
          {problems.map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      )}
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => onChange([...channels, { name: `channel-${channels.length + 1}`, subscribe: 'public', publish: 'none' }])}
      >
        <Plus className="size-4" />
        {t('websockets.addChannel')}
      </Button>
    </div>
  );
}

function Access({ label, value, options, onChange }: { label: string; value: string; options: string[]; onChange: (v: string) => void }) {
  const { t } = usePluginT();
  return (
    <div className="space-y-1.5">
      <Label>{label}</Label>
      <Select value={value} onValueChange={onChange}>
        <SelectTrigger aria-label={label}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {options.map((o) => (
            <SelectItem key={o} value={o}>
              {t(`websockets.access.${o}`)}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}

function Toggle({ id, label, checked, onChange }: { id: string; label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div className="flex items-center gap-2">
      <Switch id={id} checked={checked} onCheckedChange={onChange} />
      <Label htmlFor={id}>{label}</Label>
    </div>
  );
}
