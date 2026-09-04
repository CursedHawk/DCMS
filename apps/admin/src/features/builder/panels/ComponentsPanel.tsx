import {
  componentClassOf,
  componentPath,
  emptyComponentDefinition,
  type ComponentDefinition,
  type ComponentProp,
} from '@dcms/gjs-schema';
import { Blocks, Database, Plus, Settings2, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, cn, Input } from '@dcms/admin-ui';
import { useVfs } from '../../site-source';
import { useContentSources } from '../plugins/fields';
import { useBuilder } from '../store';

/**
 * The tenant's own components: create one, open it on the canvas, delete it.
 *
 * A component is a file in this site's repo (`blocks/<name>.json`), so this
 * panel is a manifest-and-files editor exactly like the pages panel — the actual
 * building happens on the canvas, with the binding panel in the inspector. That
 * split is deliberate: laying markup out is what the canvas is already good at,
 * and inventing a second, smaller editor for it would give authors a worse one.
 */
export function ComponentsPanel() {
  const { t } = useTranslation();
  const project = useBuilder((s) => s.project);
  const activeSlug = useBuilder((s) => s.activeSlug);
  const activeKind = useBuilder((s) => s.activeKind);
  const [creating, setCreating] = useState(false);
  const [settingsFor, setSettingsFor] = useState<string | null>(null);

  if (!project) {
    return <div className="p-4 text-sm text-muted-foreground">{t('builder.noProject')}</div>;
  }

  const open = (definition: ComponentDefinition) =>
    useBuilder.getState().setActiveTarget({ kind: 'component', slug: definition.name });

  const remove = (definition: ComponentDefinition) => {
    if (!confirm(t('builder.components.confirmDelete', { label: definition.label }))) return;
    useBuilder.getState().update((current) => ({
      ...current,
      components: current.components.filter((c) => c.name !== definition.name),
    }));
    // `update` only diffs the files a project maps to, and a removed component no
    // longer maps to any — delete it explicitly so the draft matches.
    useVfs.getState().deleteFile(componentPath(definition.name));
    if (activeKind === 'component' && activeSlug === definition.name) {
      const home = project.pages[0];
      if (home) useBuilder.getState().setActiveSlug(home.entry.slug);
    }
    // Pages that used it keep their placeholder; it renders as an unknown
    // component rather than disappearing, which is the honest outcome.
    toast.success(t('builder.components.deleted', { label: definition.label }));
  };

  const settings = settingsFor
    ? (project.components.find((c) => c.name === settingsFor) ?? null)
    : null;

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b px-3 py-2">
        <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.components.title')}
        </span>
        <Button
          size="icon"
          variant="ghost"
          onClick={() => setCreating((v) => !v)}
          title={t('builder.components.add')}
        >
          <Plus className="h-4 w-4" />
        </Button>
      </div>

      {creating && <NewComponent onDone={() => setCreating(false)} />}

      <ul className="min-h-0 flex-1 overflow-auto p-1">
        {project.components.length === 0 && !creating && (
          <li className="px-2 py-3 text-xs text-muted-foreground">{t('builder.components.empty')}</li>
        )}
        {project.components.map((definition) => {
          const isOpen = activeKind === 'component' && activeSlug === definition.name;
          return (
            <li key={definition.name}>
              <div
                className={cn(
                  'group flex items-center gap-1 rounded-md px-2 py-1.5 text-sm',
                  isOpen ? 'bg-accent text-accent-foreground' : 'hover:bg-accent/50',
                )}
              >
                <button
                  type="button"
                  className="flex min-w-0 flex-1 items-center gap-2 text-left"
                  onClick={() => open(definition)}
                >
                  {definition.source ? (
                    <Database className="h-4 w-4 shrink-0 text-muted-foreground" />
                  ) : (
                    <Blocks className="h-4 w-4 shrink-0 text-muted-foreground" />
                  )}
                  <span className="min-w-0 flex-1 truncate">{definition.label}</span>
                  {definition.source && (
                    <span className="shrink-0 text-xs text-muted-foreground">
                      {definition.source.contentType}
                    </span>
                  )}
                </button>
                <button
                  type="button"
                  title={t('builder.components.settings')}
                  onClick={() => setSettingsFor(definition.name)}
                  className="shrink-0 rounded p-1 text-muted-foreground opacity-0 hover:text-foreground group-hover:opacity-100"
                >
                  <Settings2 className="h-3.5 w-3.5" />
                </button>
                <button
                  type="button"
                  title={t('actions.delete')}
                  onClick={() => remove(definition)}
                  className="shrink-0 rounded p-1 text-muted-foreground opacity-0 hover:text-destructive group-hover:opacity-100"
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </button>
              </div>
            </li>
          );
        })}
      </ul>

      {settings && <ComponentSettings definition={settings} onClose={() => setSettingsFor(null)} />}
    </div>
  );
}

/**
 * Creating a component: a name, and whether it shows content or is just markup.
 *
 * The data source is asked for here rather than later because it decides what
 * the component *is* — with one it publishes as a placeholder filled in at run
 * time, without one it expands into the page as ordinary markup. Changing it
 * afterwards is possible in the settings, but starting from the right kind means
 * the starter template already has the right shape.
 */
function NewComponent({ onDone }: { onDone: () => void }) {
  const { t } = useTranslation();
  const { sources } = useContentSources();
  const [label, setLabel] = useState('');
  const [sourceKey, setSourceKey] = useState('');
  const [mode, setMode] = useState<'list' | 'detail'>('list');

  const chosen = sources.find((s) => keyOf(s.instanceSlug, s.contentType) === sourceKey);

  const create = () => {
    const trimmed = label.trim();
    if (!trimmed) return;
    const project = useBuilder.getState().project;
    if (!project) return;

    const name = uniqueName(slugify(trimmed), project.components);
    const definition: ComponentDefinition = {
      ...emptyComponentDefinition(name, trimmed),
      source: chosen
        ? { instanceSlug: chosen.instanceSlug, contentType: chosen.contentType, mode }
        : undefined,
      template: starterTemplate(name, chosen ? mode : null),
    };

    useBuilder.getState().update((current) => ({
      ...current,
      components: [...current.components, definition],
    }));
    useBuilder.getState().setActiveTarget({ kind: 'component', slug: name });
    onDone();
  };

  return (
    <div className="space-y-2 border-b p-2">
      <Input
        autoFocus
        value={label}
        placeholder={t('builder.components.namePlaceholder')}
        onChange={(e) => setLabel(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') create();
          if (e.key === 'Escape') onDone();
        }}
      />

      <label className="block space-y-1">
        <span className="text-xs font-medium">{t('builder.components.source')}</span>
        <select
          value={sourceKey}
          onChange={(e) => setSourceKey(e.target.value)}
          className="h-8 w-full rounded-md border bg-background px-2 text-sm"
        >
          <option value="">{t('builder.components.sourceNone')}</option>
          {sources.map((source) => (
            <option
              key={keyOf(source.instanceSlug, source.contentType)}
              value={keyOf(source.instanceSlug, source.contentType)}
            >
              {source.instanceName} — {source.contentType}
            </option>
          ))}
        </select>
      </label>

      {chosen && (
        <label className="block space-y-1">
          <span className="text-xs font-medium">{t('builder.components.mode')}</span>
          <select
            value={mode}
            onChange={(e) => setMode(e.target.value as 'list' | 'detail')}
            className="h-8 w-full rounded-md border bg-background px-2 text-sm"
          >
            <option value="list">{t('builder.components.modeList')}</option>
            <option value="detail" disabled={!chosen.hasSlug}>
              {t('builder.components.modeDetail')}
            </option>
          </select>
        </label>
      )}

      <div className="flex gap-1">
        <Button size="sm" className="flex-1" onClick={create}>
          {t('actions.add')}
        </Button>
        <Button size="sm" variant="ghost" onClick={onDone}>
          {t('actions.cancel')}
        </Button>
      </div>
    </div>
  );
}

/**
 * A component's own settings: what it is called, what it is bound to, and the
 * settings it offers whoever drops it on a page.
 *
 * Props are the reason a component can be reused rather than copied: a "Latest
 * posts" component with a `heading` prop is one component on five pages, not
 * five near-identical components.
 */
function ComponentSettings({
  definition,
  onClose,
}: {
  definition: ComponentDefinition;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const { sources } = useContentSources();

  const patch = (changes: Partial<ComponentDefinition>) => {
    useBuilder.getState().update((current) => ({
      ...current,
      components: current.components.map((c) =>
        c.name === definition.name ? { ...c, ...changes } : c,
      ),
    }));
  };

  const setSource = (key: string) => {
    const chosen = sources.find((s) => keyOf(s.instanceSlug, s.contentType) === key);
    patch({
      source: chosen
        ? {
            instanceSlug: chosen.instanceSlug,
            contentType: chosen.contentType,
            mode: definition.source?.mode ?? 'list',
          }
        : undefined,
    });
  };

  const setProps = (props: ComponentProp[]) => patch({ props });

  return (
    <div className="border-t bg-muted/30 p-3">
      <div className="mb-2 flex items-center justify-between">
        <span className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.components.settings')}
        </span>
        <Button size="sm" variant="ghost" onClick={onClose}>
          {t('actions.close')}
        </Button>
      </div>

      <label className="mb-2 block space-y-1">
        <span className="text-xs font-medium">{t('builder.components.label')}</span>
        <Input
          className="h-8"
          value={definition.label}
          onChange={(e) => patch({ label: e.target.value })}
        />
      </label>

      <label className="mb-2 block space-y-1">
        <span className="text-xs font-medium">{t('builder.components.source')}</span>
        <select
          value={
            definition.source
              ? keyOf(definition.source.instanceSlug, definition.source.contentType)
              : ''
          }
          onChange={(e) => setSource(e.target.value)}
          className="h-8 w-full rounded-md border bg-background px-2 text-sm"
        >
          <option value="">{t('builder.components.sourceNone')}</option>
          {sources.map((source) => (
            <option
              key={keyOf(source.instanceSlug, source.contentType)}
              value={keyOf(source.instanceSlug, source.contentType)}
            >
              {source.instanceName} — {source.contentType}
            </option>
          ))}
        </select>
      </label>

      {definition.source && (
        <label className="mb-2 block space-y-1">
          <span className="text-xs font-medium">{t('builder.components.mode')}</span>
          <select
            value={definition.source.mode}
            onChange={(e) =>
              patch({ source: { ...definition.source!, mode: e.target.value as 'list' | 'detail' } })
            }
            className="h-8 w-full rounded-md border bg-background px-2 text-sm"
          >
            <option value="list">{t('builder.components.modeList')}</option>
            <option value="detail">{t('builder.components.modeDetail')}</option>
          </select>
        </label>
      )}

      <div className="mt-3 space-y-2">
        <div className="flex items-center justify-between">
          <span className="text-xs font-medium">{t('builder.components.props')}</span>
          <Button
            size="sm"
            variant="ghost"
            onClick={() =>
              setProps([
                ...definition.props,
                {
                  name: uniquePropName(definition.props),
                  label: t('builder.components.newProp'),
                  kind: 'text',
                },
              ])
            }
          >
            <Plus className="h-3.5 w-3.5" />
          </Button>
        </div>
        <p className="text-xs text-muted-foreground">{t('builder.components.propsHint')}</p>

        {definition.props.map((prop, index) => (
          <div key={index} className="flex items-center gap-1">
            <Input
              className="h-8 min-w-0 flex-1"
              value={prop.label}
              onChange={(e) =>
                setProps(
                  definition.props.map((p, i) => (i === index ? { ...p, label: e.target.value } : p)),
                )
              }
            />
            <select
              value={prop.kind}
              onChange={(e) =>
                setProps(
                  definition.props.map((p, i) =>
                    i === index ? { ...p, kind: e.target.value as ComponentProp['kind'] } : p,
                  ),
                )
              }
              className="h-8 w-24 shrink-0 rounded-md border bg-background px-1 text-xs"
            >
              {(['text', 'longText', 'number', 'checkbox', 'color', 'url', 'media'] as const).map(
                (kind) => (
                  <option key={kind} value={kind}>
                    {kind}
                  </option>
                ),
              )}
            </select>
            <button
              type="button"
              title={t('actions.delete')}
              onClick={() => setProps(definition.props.filter((_, i) => i !== index))}
              className="shrink-0 rounded p-1 text-muted-foreground hover:text-destructive"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}

function keyOf(instanceSlug: string, contentType: string): string {
  return `${instanceSlug}::${contentType}`;
}

/**
 * What a new component starts as.
 *
 * A dynamic list starts with the repeat already in place and one bound heading,
 * because an empty template gives the binding panel nothing to bind and the
 * author no way to see that the component works at all. A snippet starts as a
 * plain band, which is what "some markup I want to reuse" looks like.
 */
function starterTemplate(name: string, mode: 'list' | 'detail' | null): string {
  const root = componentClassOf(name);
  if (mode === 'list') {
    return `<div class="${root}">
  <article class="dcms-card" data-dcms-repeat>
    <h3 class="dcms-card-title" data-dcms-bind="text:title"></h3>
  </article>
  <p class="dcms-empty" data-dcms-empty>Nothing here yet.</p>
</div>
`;
  }
  if (mode === 'detail') {
    return `<article class="${root}">
  <h1 data-dcms-bind="text:title"></h1>
</article>
`;
  }
  return `<div class="${root} dcms-section">
  <div class="dcms-container">
    <h2>New component</h2>
  </div>
</div>
`;
}

function slugify(value: string): string {
  return (
    value
      .normalize('NFKD')
      .replace(/[\u0300-\u036f]/g, '')
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '') || 'component'
  );
}

function uniqueName(base: string, existing: readonly ComponentDefinition[]): string {
  const taken = new Set(existing.map((c) => c.name));
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base}-${n}`)) n += 1;
  return `${base}-${n}`;
}

function uniquePropName(existing: readonly ComponentProp[]): string {
  const taken = new Set(existing.map((p) => p.name));
  let n = 1;
  while (taken.has(`prop${n}`)) n += 1;
  return `prop${n}`;
}
