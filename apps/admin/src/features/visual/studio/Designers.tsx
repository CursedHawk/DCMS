import { PROP_GROUPS, type Registry } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { ArrowDown, ArrowUp, Link2, Plus, SquareDashed, X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@dcms/ui';
import { findNode } from '../canvas/operations';
import {
  LIKELY_GROUPS,
  editSetting,
  editSlot,
  exposeLink,
  exposeProp,
  exposeSlot,
  exposedAs,
  linkExposedAs,
  moveSetting,
  templateParts,
  templateSlots,
  unexpose,
  unexposeSlot,
} from './model';
import type { StudioDoc } from './useStudioDoc';

type Studio = Pick<StudioDoc, 'doc' | 'change'>;

/** Show the author which part a setting or slot belongs to. */
function point(editor: Editor | null, nodeId: string | undefined) {
  const c = editor && nodeId ? findNode(editor, nodeId) : undefined;
  if (!c || !editor) return;
  editor.select(c);
  c.getEl()?.scrollIntoView({ block: 'center', behavior: 'smooth' });
}

/**
 * Which settings pages get (Mode D v2, U4.2): the ones already given, in the order the inspector
 * shows them, each with the label, help and group a page author sees; then what each part of the
 * template could offer. Picking a setting points at its part on the canvas.
 */
export function SettingsDesigner({ editor, registry, studio }: { editor: Editor | null; registry: Registry; studio: Studio }) {
  const { t } = useTranslation();
  const { doc, change } = studio;
  const [all, setAll] = useState(false);
  const parts = templateParts(doc, registry);

  return (
    <div className="space-y-4 p-3 text-sm">
      <p className="text-xs text-muted-foreground">{t('visual.studio.settingsHint', { label: doc.label })}</p>
      <section aria-labelledby="studio-given" className="space-y-2">
        <h3 id="studio-given" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('visual.studio.given')}
        </h3>
        {doc.props.length === 0 && <p className="text-xs text-muted-foreground">{t('visual.studio.noSettings')}</p>}
        {doc.props.map((p, i) => (
          <div key={p.name} role="group" aria-label={p.label} className="space-y-1.5 rounded-md border p-2">
            <div className="flex items-center gap-1">
              <button type="button" onClick={() => point(editor, doc.bindings[p.name]?.[0]?.node)} className="min-w-0 flex-1 truncate text-left text-xs text-muted-foreground hover:text-foreground">
                {p.name} · {p.kind}
              </button>
              <Button size="icon" variant="ghost" className="h-6 w-6" disabled={i === 0} onClick={() => change((d) => moveSetting(d, p.name, -1))} aria-label={t('visual.studio.earlier', { name: p.label })}>
                <ArrowUp className="h-3.5 w-3.5" />
              </Button>
              <Button size="icon" variant="ghost" className="h-6 w-6" disabled={i === doc.props.length - 1} onClick={() => change((d) => moveSetting(d, p.name, 1))} aria-label={t('visual.studio.later', { name: p.label })}>
                <ArrowDown className="h-3.5 w-3.5" />
              </Button>
              <Button size="icon" variant="ghost" className="h-6 w-6" onClick={() => change((d) => unexpose(d, p.name))} aria-label={t('visual.studio.remove', { name: p.label })}>
                <X className="h-3.5 w-3.5" />
              </Button>
            </div>
            <Field label={t('visual.studio.label')} value={p.label} onCommit={(label) => label && change((d) => editSetting(d, p.name, { label }))} />
            <Field label={t('visual.studio.help')} value={p.description ?? ''} onCommit={(description) => change((d) => editSetting(d, p.name, { description: description || undefined }))} />
            <div className="space-y-1">
              <Label className="text-xs">{t('visual.studio.group')}</Label>
              <Select value={p.group ?? 'content'} onValueChange={(group) => change((d) => editSetting(d, p.name, { group: group as (typeof PROP_GROUPS)[number] }))}>
                <SelectTrigger className="h-7 text-xs" aria-label={t('visual.studio.groupOf', { name: p.label })}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {PROP_GROUPS.map((g) => (
                    <SelectItem key={g} value={g}>
                      {t(`visual.groups.${g}`)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>
        ))}
      </section>

      <section aria-labelledby="studio-could" className="space-y-2">
        <div className="flex items-center justify-between">
          <h3 id="studio-could" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            {t('visual.studio.could')}
          </h3>
          <label className="flex items-center gap-1 text-xs text-muted-foreground">
            <input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} /> {t('visual.studio.showAll')}
          </label>
        </div>
        {parts.map(({ node, definition, title }) => {
          const offers = definition.props.filter((p) => (all || LIKELY_GROUPS.has(p.group ?? 'content')) && !exposedAs(doc, node.id, p.name));
          const link = definition.actions?.includes('navigate') && !linkExposedAs(doc, node.id);
          if (!offers.length && !link) return null;
          return (
            <div key={node.id} className="rounded-md border p-2">
              <button type="button" onClick={() => point(editor, node.id)} className="mb-1 block w-full truncate text-left text-xs font-medium hover:underline">
                {title}
              </button>
              <div className="flex flex-wrap gap-1">
                {offers.map((p) => (
                  <Button key={p.name} size="sm" variant="outline" className="h-7 text-xs" onClick={() => change((d) => exposeProp(d, node.id, definition, p, node.props?.[p.name]))}>
                    <Plus className="h-3 w-3" /> {p.label}
                  </Button>
                ))}
                {link && (
                  <Button size="sm" variant="outline" className="h-7 text-xs" onClick={() => change((d) => exposeLink(d, node.id, `${definition.label}: ${t('visual.expose.link')}`))}>
                    <Link2 className="h-3 w-3" /> {t('visual.expose.link')}
                  </Button>
                )}
              </div>
            </div>
          );
        })}
      </section>
    </div>
  );
}

/**
 * Which places pages fill (Mode D v2, U4.3): every slot in the template — the empty ones can
 * become places pages put their own parts into, with a label, which kinds of part, and how many.
 */
export function SlotDesigner({ editor, registry, studio }: { editor: Editor | null; registry: Registry; studio: Studio }) {
  const { t } = useTranslation();
  const { doc, change } = studio;
  const placeable = [...registry.values()].filter((d) => d.draggable !== false && d.allowedParents?.length !== 0 && d.category !== 'Page');
  const exposed = new Map(Object.entries(doc.slotTargets).map(([name, s]) => [`${s.node}:${s.slot}`, name]));

  return (
    <div className="space-y-4 p-3 text-sm">
      <p className="text-xs text-muted-foreground">{t('visual.studio.slotsHint', { label: doc.label })}</p>
      {doc.slots.map((s) => (
        <div key={s.name} role="group" aria-label={s.label ?? s.name} className="space-y-1.5 rounded-md border p-2">
          <div className="flex items-center gap-1">
            <button type="button" onClick={() => point(editor, doc.slotTargets[s.name]?.node)} className="min-w-0 flex-1 truncate text-left text-xs text-muted-foreground hover:text-foreground">
              <SquareDashed className="mr-1 inline h-3.5 w-3.5" /> {s.name}
            </button>
            <Button size="icon" variant="ghost" className="h-6 w-6" onClick={() => change((d) => unexposeSlot(d, s.name))} aria-label={t('visual.studio.remove', { name: s.label ?? s.name })}>
              <X className="h-3.5 w-3.5" />
            </Button>
          </div>
          <Field label={t('visual.studio.label')} value={s.label ?? ''} onCommit={(label) => change((d) => editSlot(d, s.name, { label: label || undefined }))} />
          <div className="space-y-1">
            <Label htmlFor={`max-${s.name}`} className="text-xs">{t('visual.studio.max')}</Label>
            <Input
              id={`max-${s.name}`}
              type="number"
              min={1}
              className="h-7 w-24 text-xs"
              defaultValue={s.max ?? ''}
              placeholder={t('visual.studio.any')}
              onBlur={(e) => change((d) => editSlot(d, s.name, { max: Number(e.target.value) || undefined }))}
            />
          </div>
          <details className="text-xs">
            <summary className="cursor-pointer text-muted-foreground">
              {s.allowed?.length ? t('visual.studio.allowedSome', { count: s.allowed.length }) : t('visual.studio.allowedAny')}
            </summary>
            <div className="mt-1 grid grid-cols-2 gap-x-2 gap-y-0.5">
              {placeable.map((d) => (
                <label key={d.type} className="flex items-center gap-1">
                  <input
                    type="checkbox"
                    checked={s.allowed?.includes(d.type) ?? false}
                    onChange={(e) =>
                      change((doc2) =>
                        editSlot(doc2, s.name, {
                          allowed: e.target.checked ? [...(s.allowed ?? []), d.type] : (s.allowed ?? []).filter((x) => x !== d.type),
                        }),
                      )
                    }
                  />
                  {d.label}
                </label>
              ))}
            </div>
          </details>
        </div>
      ))}

      <section aria-labelledby="studio-areas" className="space-y-1">
        <h3 id="studio-areas" className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {t('visual.studio.areas')}
        </h3>
        {templateSlots(doc, registry)
          .filter((e) => !exposed.has(`${e.part.node.id}:${e.slot}`))
          .map((e) => (
            <div key={`${e.part.node.id}:${e.slot}`} className="flex items-center gap-2 rounded-md border p-2 text-xs">
              <button type="button" onClick={() => point(editor, e.part.node.id)} className="min-w-0 flex-1 truncate text-left hover:underline">
                {e.part.title} › {e.label}
              </button>
              {e.empty ? (
                <Button size="sm" variant="outline" className="h-7 text-xs" onClick={() => change((d) => exposeSlot(d, e.part.node.id, e.slot, e.label))}>
                  <Plus className="h-3 w-3" /> {t('visual.studio.letPagesFill')}
                </Button>
              ) : (
                <span className="text-muted-foreground">{t('visual.studio.notEmpty')}</span>
              )}
            </div>
          ))}
      </section>
    </div>
  );
}

function Field({ label, value, onCommit }: { label: string; value: string; onCommit: (v: string) => void }) {
  const [draft, setDraft] = useState<string | null>(null);
  const commit = () => {
    if (draft !== null && draft.trim() !== value) onCommit(draft.trim());
    setDraft(null);
  };
  return (
    <div className="space-y-1">
      <Label className="text-xs">{label}</Label>
      <Input aria-label={label} value={draft ?? value} onChange={(e) => setDraft(e.target.value)} onBlur={commit} onKeyDown={(e) => e.key === 'Enter' && commit()} className="h-7 text-xs" />
    </div>
  );
}
