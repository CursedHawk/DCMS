import { type ComponentDefinition, type PropDefinition, type TenantComponentDoc } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { ArrowUpCircle, Link2, Plug, PlugZap, SquareDashed } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@dcms/ui';
import { useVfs } from '../site-source';
import { EXTRA, ID, PROPS, SLOT, SLOT_TYPE, type NodeExtra } from './canvas/tree';
import { migrateInstance, readComponent, updateComponent } from './documents';
import { useVisual } from './store';

function uniqueName(base: string, taken: ReadonlySet<string>): string {
  let name = base;
  for (let n = 2; taken.has(name); n++) name = `${base}${n}`;
  return name;
}

/**
 * Composer controls for the node selected inside a component's template: which of its settings,
 * its link and its empty slots the component exposes to the pages that use it (P3.2).
 */
export function ExposePanel({ selected, definition }: { selected: Component; definition: ComponentDefinition }) {
  const { t } = useTranslation();
  const target = useVisual((s) => s.target);
  useVfs((s) => (target?.kind === 'component' ? s.files[`dcms/components/${target.name}/v${target.version}.json`] : undefined));
  if (target?.kind !== 'component') return null;
  const doc = readComponent(target.name, target.version);
  if (!doc) return null;

  const nodeId = selected.get(ID) as string;
  const props = (selected.get(PROPS) ?? {}) as Record<string, unknown>;
  const change = (fn: (d: TenantComponentDoc) => TenantComponentDoc) => {
    useVisual.getState().flushCanvas();
    const result = updateComponent(target.name, target.version, fn);
    if (!result.ok) toast.error(result.error);
  };

  const exposedAs = (prop: string) =>
    Object.entries(doc.bindings).find(([, ts]) => ts.some((b) => b.node === nodeId && 'prop' in b && b.prop === prop))?.[0];
  const linkExposedAs = Object.entries(doc.bindings).find(([, ts]) => ts.some((b) => b.node === nodeId && 'action' in b))?.[0];

  const expose = (prop: PropDefinition) =>
    change((d) => {
      const name = uniqueName(prop.name, new Set(d.props.map((p) => p.name)));
      const current = props[prop.name];
      const exposed = { ...prop, name, label: `${definition.label}: ${prop.label}` } as PropDefinition;
      if (current !== undefined && 'default' in exposed) (exposed as { default?: unknown }).default = current;
      return { ...d, props: [...d.props, exposed], bindings: { ...d.bindings, [name]: [{ node: nodeId, prop: prop.name }] } };
    });
  const unexpose = (name: string) =>
    change((d) => {
      const bindings = { ...d.bindings };
      delete bindings[name];
      return { ...d, props: d.props.filter((p) => p.name !== name), bindings };
    });

  const emptySlots = (definition.slots ?? []).filter((slot) => {
    const slotModel = selected.components().models.find((c) => c.get('type') === SLOT_TYPE && c.get(SLOT) === slot.name);
    return slotModel && slotModel.components().length === 0;
  });
  const slotExposedAs = (slot: string) =>
    Object.entries(doc.slotTargets).find(([, s]) => s.node === nodeId && s.slot === slot)?.[0];

  return (
    <div className="space-y-2 border-t pt-4">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('visual.expose.title')}</div>
      <p className="text-xs text-muted-foreground">{t('visual.expose.hint', { label: doc.label })}</p>
      {definition.props.map((prop) => {
        const as = exposedAs(prop.name);
        return (
          <Button key={prop.name} size="sm" variant={as ? 'secondary' : 'ghost'} className="w-full justify-start" onClick={() => (as ? unexpose(as) : expose(prop))}>
            {as ? <PlugZap className="h-4 w-4" /> : <Plug className="h-4 w-4" />}
            {as ? t('visual.expose.exposedAs', { prop: prop.label, name: as }) : t('visual.expose.prop', { prop: prop.label })}
          </Button>
        );
      })}
      {definition.actions?.includes('navigate') && (
        <Button
          size="sm"
          variant={linkExposedAs ? 'secondary' : 'ghost'}
          className="w-full justify-start"
          onClick={() =>
            linkExposedAs
              ? unexpose(linkExposedAs)
              : change((d) => {
                  const name = uniqueName('link', new Set(d.props.map((p) => p.name)));
                  return {
                    ...d,
                    props: [...d.props, { kind: 'url', name, label: `${definition.label}: ${t('visual.expose.link')}` }],
                    bindings: { ...d.bindings, [name]: [{ node: nodeId, action: 'link' }] },
                  };
                })
          }
        >
          <Link2 className="h-4 w-4" /> {linkExposedAs ? t('visual.expose.linkExposed') : t('visual.expose.exposeLink')}
        </Button>
      )}
      {emptySlots.map((slot) => {
        const as = slotExposedAs(slot.name);
        return (
          <Button
            key={slot.name}
            size="sm"
            variant={as ? 'secondary' : 'ghost'}
            className="w-full justify-start"
            onClick={() =>
              change((d) => {
                if (as) {
                  const slotTargets = { ...d.slotTargets };
                  delete slotTargets[as];
                  return { ...d, slots: d.slots.filter((s) => s.name !== as), slotTargets };
                }
                const name = uniqueName(slot.name === 'default' ? 'content' : slot.name, new Set(d.slots.map((s) => s.name)));
                return {
                  ...d,
                  slots: [...d.slots, { name, label: slot.label ?? slot.name }],
                  slotTargets: { ...d.slotTargets, [name]: { node: nodeId, slot: slot.name } },
                };
              })
            }
          >
            <SquareDashed className="h-4 w-4" />
            {as ? t('visual.expose.slotExposed', { slot: slot.label ?? slot.name }) : t('visual.expose.slot', { slot: slot.label ?? slot.name })}
          </Button>
        );
      })}
    </div>
  );
}

/** On a page: an instance of an older version of a site component, and the way to move it on (P3.5). */
export function InstanceVersion({ selected, latest }: { selected: Component; latest: ComponentDefinition }) {
  const { t } = useTranslation();
  const extra = (selected.get(EXTRA) ?? {}) as NodeExtra;
  if (!latest.template || extra.version === undefined || extra.version === latest.version) return null;
  const update = () => {
    const node = { id: selected.get(ID) as string, type: latest.type, version: extra.version, props: selected.get(PROPS) as Record<string, unknown> };
    const { node: migrated, dropped } = migrateInstance(node, latest.template!);
    selected.set(PROPS, migrated.props ?? {});
    selected.set(EXTRA, { ...extra, version: latest.version });
    // Slots the new version adds need somewhere for GrapesJS to draw them.
    for (const slot of latest.slots ?? []) {
      const has = selected.components().models.some((c) => c.get('type') === SLOT_TYPE && c.get(SLOT) === slot.name);
      if (!has) selected.append({ type: SLOT_TYPE, [SLOT]: slot.name, name: slot.label ?? slot.name });
    }
    toast.success(dropped.length ? t('visual.mine.updatedDropped', { count: 1, dropped: dropped.join(', ') }) : t('visual.mine.updated', { count: 1 }));
  };
  return (
    <div className="flex items-center gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 p-2 text-xs">
      <span className="min-w-0 flex-1">{t('visual.mine.instanceOutdated', { version: extra.version, latest: latest.version })}</span>
      <Button size="sm" variant="outline" onClick={update}>
        <ArrowUpCircle className="h-4 w-4" /> {t('visual.mine.updateOne', { version: latest.version })}
      </Button>
    </div>
  );
}
