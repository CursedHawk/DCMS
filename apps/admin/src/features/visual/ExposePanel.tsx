import { type ComponentDefinition, type PropDefinition, type TenantComponentDoc } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import { ArrowUpCircle, Link2, Plug, PlugZap, SquareDashed } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button } from '@dcms/ui';
import { useVfs } from '../site-source';
import { EXTRA, ID, PROPS, SLOT, SLOT_TYPE, fromGrapes, type NodeExtra } from './canvas/tree';
import { migrateInstance, readComponent, updateComponent, versionChanges } from './documents';
import { useVisual } from './store';
import { exposeLink, exposeProp, exposeSlot, exposedAs, linkExposedAs, slotExposedAs, unexpose, unexposeSlot } from './studio/model';

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

  const exposed = (prop: string) => exposedAs(doc, nodeId, prop);
  const link = linkExposedAs(doc, nodeId);
  const expose = (prop: PropDefinition) => change((d) => exposeProp(d, nodeId, definition, prop, props[prop.name]));
  const drop = (name: string) => change((d) => unexpose(d, name));

  const emptySlots = (definition.slots ?? []).filter((slot) => {
    const slotModel = selected.components().models.find((c) => c.get('type') === SLOT_TYPE && c.get(SLOT) === slot.name);
    return slotModel && slotModel.components().length === 0;
  });

  return (
    <div className="space-y-2 border-t pt-4">
      <div className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('visual.expose.title')}</div>
      <p className="text-xs text-muted-foreground">{t('visual.expose.hint', { label: doc.label })}</p>
      {definition.props.map((prop) => {
        const as = exposed(prop.name);
        return (
          <Button key={prop.name} size="sm" variant={as ? 'secondary' : 'ghost'} className="w-full justify-start" onClick={() => (as ? drop(as) : expose(prop))}>
            {as ? <PlugZap className="h-4 w-4" /> : <Plug className="h-4 w-4" />}
            {as ? t('visual.expose.exposedAs', { prop: prop.label, name: as }) : t('visual.expose.prop', { prop: prop.label })}
          </Button>
        );
      })}
      {definition.actions?.includes('navigate') && (
        <Button
          size="sm"
          variant={link ? 'secondary' : 'ghost'}
          className="w-full justify-start"
          onClick={() => (link ? drop(link) : change((d) => exposeLink(d, nodeId, `${definition.label}: ${t('visual.expose.link')}`)))}
        >
          <Link2 className="h-4 w-4" /> {link ? t('visual.expose.linkExposed') : t('visual.expose.exposeLink')}
        </Button>
      )}
      {emptySlots.map((slot) => {
        const as = slotExposedAs(doc, nodeId, slot.name);
        return (
          <Button
            key={slot.name}
            size="sm"
            variant={as ? 'secondary' : 'ghost'}
            className="w-full justify-start"
            onClick={() => change((d) => (as ? unexposeSlot(d, as) : exposeSlot(d, nodeId, slot.name, slot.label ?? slot.name)))}
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
  const from = latest.olderVersions?.[extra.version]?.template;
  const changes = versionChanges(fromGrapes(selected, new Set()), from, latest.template);
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
      <div className="min-w-0 flex-1 space-y-1">
        <div>{t('visual.mine.instanceOutdated', { version: extra.version, latest: latest.version })}</div>
        {(changes.removed.length > 0 || changes.added.length > 0 || changes.hiddenSlots.length > 0) && (
          <ul className="list-disc pl-4 text-muted-foreground" aria-label={t('visual.mine.changesTitle')}>
            {changes.removed.map((r) => (
              <li key={`r:${r.name}`}>{t('visual.mine.changeRemoved', { name: r.name, value: String(r.value) })}</li>
            ))}
            {changes.added.length > 0 && <li>{t('visual.mine.changeAdded', { names: changes.added.join(', ') })}</li>}
            {changes.hiddenSlots.map((s) => (
              <li key={`s:${s}`}>{t('visual.mine.changeHidden', { slot: s })}</li>
            ))}
          </ul>
        )}
      </div>
      <Button size="sm" variant="outline" onClick={update}>
        <ArrowUpCircle className="h-4 w-4" /> {t('visual.mine.updateOne', { version: latest.version })}
      </Button>
    </div>
  );
}
