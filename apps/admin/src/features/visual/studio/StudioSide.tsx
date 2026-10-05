import { tenantType, type PropDefinition, type Registry, type TenantComponentDoc } from '@dcms/site-runtime';
import { Eye, Monitor, Smartphone, Tablet, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, Dialog, DialogContent, DialogHeader, DialogTitle, Input, Label, Switch, cn } from '@dcms/ui';
import { componentUsage, deleteComponentVersion, listComponents, readComponent, updateAllInstances, versionToEdit } from '../documents';
import { LivePreview } from '../palette/LivePreview';
import { useVisual } from '../store';
import type { StudioDoc } from './useStudioDoc';

const DEVICES = [
  { id: 'desktop', width: 1100, icon: Monitor },
  { id: 'tablet', width: 768, icon: Tablet },
  { id: 'mobile', width: 375, icon: Smartphone },
] as const;

/**
 * An instance as a page would place it (Mode D v2, U4.4): the settings filled with sample values
 * the author can change here — without touching any page — at desktop, tablet or phone width, in
 * a dialog large enough to read it.
 */
export function InstancePreview({ registry, studio }: { registry: Registry; studio: StudioDoc }) {
  const { t } = useTranslation();
  const app = useVisual((s) => s.app);
  const { doc, name, version } = studio;
  const [samples, setSamples] = useState<Record<string, unknown>>({});
  const [device, setDevice] = useState<(typeof DEVICES)[number]['id']>('desktop');
  const [open, setOpen] = useState(false);
  const props = useMemo(() => Object.fromEntries(doc.props.map((p) => [p.name, p.name in samples ? samples[p.name] : 'default' in p ? p.default : undefined])), [doc.props, samples]);
  const node = useMemo(() => ({ id: 'studio-preview', type: tenantType(name), version, props }), [name, version, props]);
  const width = DEVICES.find((d) => d.id === device)!.width;

  const samplesList = doc.props.map((p) => <Sample key={p.name} prop={p} value={props[p.name]} onChange={(v) => setSamples((s) => ({ ...s, [p.name]: v }))} />);
  return (
    <div className="space-y-3 p-3 text-sm">
      <p className="text-xs text-muted-foreground">{t('visual.studio.previewHint')}</p>
      <Button size="sm" variant="outline" className="w-full" onClick={() => setOpen(true)}>
        <Eye className="h-4 w-4" /> {t('visual.studio.showPreview')}
      </Button>
      {doc.props.length > 0 && <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('visual.studio.samples')}</h3>}
      {samplesList}
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="flex h-[85vh] max-w-6xl flex-col">
          <DialogHeader>
            <DialogTitle>{t('visual.studio.previewTitle', { label: doc.label })}</DialogTitle>
          </DialogHeader>
          <div className="flex min-h-0 flex-1 gap-4">
            <div className="flex min-w-0 flex-1 flex-col gap-2">
              <div role="group" aria-label={t('visual.device')} className="flex gap-1">
                {DEVICES.map(({ id, icon: Icon }) => (
                  <Button key={id} size="icon" variant={device === id ? 'secondary' : 'ghost'} className="h-7 w-7" aria-pressed={device === id} title={t(`visual.devices.${id}`)} onClick={() => setDevice(id)}>
                    <Icon className="h-4 w-4" />
                  </Button>
                ))}
              </div>
              {/* At the device's own width when it fits, scaled down when it does not. */}
              <div className="min-h-0 flex-1 overflow-auto rounded-md border bg-muted/30 p-4">
                <div className="relative mx-auto h-full max-w-full" style={{ width }}>
                  <LivePreview node={node} registry={registry} app={app} mode="edit" layoutWidth={width} />
                </div>
              </div>
            </div>
            {doc.props.length > 0 && <aside className="w-64 shrink-0 space-y-3 overflow-y-auto">{samplesList}</aside>}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

/** One sample value: the plain kinds get a control; the rest show as the component has them. */
function Sample({ prop, value, onChange }: { prop: PropDefinition; value: unknown; onChange: (v: unknown) => void }) {
  const id = `sample-${prop.name}`;
  if (prop.kind === 'boolean') {
    return (
      <label className="flex items-center gap-2 text-xs">
        <Switch checked={value === true} onCheckedChange={onChange} /> {prop.label}
      </label>
    );
  }
  if (prop.kind === 'select') {
    return (
      <div className="space-y-1">
        <Label htmlFor={id} className="text-xs">{prop.label}</Label>
        <select id={id} value={String(value ?? '')} onChange={(e) => onChange(e.target.value)} className="h-7 w-full rounded-md border bg-background px-2 text-xs">
          {prop.options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </div>
    );
  }
  if (prop.kind === 'text' || prop.kind === 'url' || prop.kind === 'number') {
    return (
      <div className="space-y-1">
        <Label htmlFor={id} className="text-xs">{prop.label}</Label>
        <Input
          id={id}
          type={prop.kind === 'number' ? 'number' : 'text'}
          value={value === undefined ? '' : String(value)}
          onChange={(e) => onChange(prop.kind === 'number' ? Number(e.target.value) : e.target.value)}
          className="h-7 text-xs"
        />
      </div>
    );
  }
  return null;
}

/** What changed between two versions' settings and slots, in words. */
function difference(from: TenantComponentDoc | null, to: TenantComponentDoc): { added: string[]; removed: string[] } {
  const names = (d: TenantComponentDoc | null) => new Set([...(d?.props ?? []).map((p) => p.label), ...(d?.slots ?? []).map((s) => s.label ?? s.name)]);
  const a = names(from);
  const b = names(to);
  return { added: [...b].filter((x) => !a.has(x)), removed: [...a].filter((x) => !b.has(x)) };
}

/**
 * Every version (Mode D v2, U4.5): how many instances use each, what each changed from the one
 * before, and the moves between them — start the next version, open one, update every instance
 * to the latest, delete a version nothing uses.
 */
export function VersionsPanel({ studio }: { studio: StudioDoc }) {
  const { t } = useTranslation();
  const { name, version } = studio;
  const info = listComponents().find((c) => c.name === name);
  if (!info) return null;
  const usage = componentUsage(name);
  const latest = info.latest.version;
  const older = [...usage.entries()].filter(([v]) => v !== latest).reduce((n, [, c]) => n + c, 0);
  const open = (v: number) => useVisual.getState().setTarget({ kind: 'component', name, version: v });

  return (
    <div className="space-y-3 p-3 text-sm">
      {usage.get(latest) ? (
        <Button
          size="sm"
          variant="outline"
          className="w-full"
          onClick={() => {
            const pick = versionToEdit(name);
            if (pick) open(pick.version);
          }}
        >
          {t('visual.studio.startVersion', { version: latest + 1 })}
        </Button>
      ) : null}
      {older > 0 && (
        <div className="space-y-1 rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs">
          <p>{t('visual.mine.outdated', { count: older, version: latest })}</p>
          <Button
            size="sm"
            variant="outline"
            onClick={() => {
              const out = updateAllInstances(name);
              toast.success(t('visual.mine.updated', { count: out.updated }));
            }}
          >
            {t('visual.mine.updateAll')}
          </Button>
        </div>
      )}
      <ol className="space-y-2" aria-label={t('visual.studio.tabs.versions')}>
        {[...info.versions].reverse().map((v) => {
          const doc = readComponent(name, v);
          if (!doc) return null;
          const changes = difference(v > 1 ? readComponent(name, v - 1) : null, doc);
          const used = usage.get(v) ?? 0;
          return (
            <li key={v} className={cn('rounded-md border p-2 text-xs', v === version && 'border-primary')}>
              <div className="flex items-center gap-2">
                <span className="font-medium">v{v}</span>
                <span className="text-muted-foreground">{used ? t('visual.mine.used', { count: used }) : t('visual.studio.unused')}</span>
                <div className="flex-1" />
                {v !== version && (
                  <Button size="sm" variant="ghost" className="h-6 text-xs" onClick={() => open(v)}>
                    {t('visual.studio.open')}
                  </Button>
                )}
                {!used && info.versions.length > 1 && (
                  <Button
                    size="icon"
                    variant="ghost"
                    className="h-6 w-6"
                    aria-label={t('visual.studio.deleteVersion', { version: v })}
                    onClick={() => {
                      const out = deleteComponentVersion(name, v);
                      if (!out.ok) return void toast.error(out.error);
                      if (v === version) open(info.versions.filter((x) => x !== v).at(-1)!);
                    }}
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                )}
              </div>
              {v > 1 && (changes.added.length > 0 || changes.removed.length > 0) && (
                <ul className="mt-1 list-disc pl-4 text-muted-foreground">
                  {changes.added.length > 0 && <li>{t('visual.studio.added', { names: changes.added.join(', ') })}</li>}
                  {changes.removed.length > 0 && <li>{t('visual.studio.removed', { names: changes.removed.join(', ') })}</li>}
                </ul>
              )}
            </li>
          );
        })}
      </ol>
    </div>
  );
}
