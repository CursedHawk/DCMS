import {
  BINDABLE,
  CODE_PREFIX,
  codeSourcePath,
  nodeCssSchema,
  actionSchema,
  propValueSchema,
  sourceKey,
  type Action,
  type ComponentDefinition,
  type PropDefinition,
  type Registry,
  type Responsive,
  type Source,
} from '@dcms/site-runtime';
import type { ContentField } from '@dcms/gjs-blocks';
import type { Component, Editor } from 'grapesjs';
import { FileCode, RotateCcw } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Button,
  Input,
  Label,
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Textarea,
} from '@dcms/ui';
import { MediaPicker } from '../media/MediaPicker';
import { assetIdFrom, mediaUrlFor } from '../builder/panels/TraitsPanel';
import { useEditorEvent, useSelected } from '../builder/panels/useEditorEvent';
import { EXTRA, ID, PROPS, type NodeExtra } from './canvas/tree';
import { ExposePanel, InstanceVersion } from './ExposePanel';
import { META_BINDABLE, scopeOf, useContentCatalog } from './data';
import { readPage } from './documents';
import { useVisual } from './store';
import { useVfs } from '../site-source/vfs';

/**
 * The inspector: the selected component's props, one control per declared prop.
 *
 * The controls come from the component's definition, so a prop added to a component appears
 * here with no inspector code. Every value is checked against the prop's own schema
 * (`propValueSchema`) before it is written — the same rule the validator and the AI tools
 * apply — so what can be typed here is exactly what can be saved.
 *
 * **Devices.** With the canvas on Tablet or Mobile, a prop declared `responsive` edits that
 * device's override (`node.responsive`) instead of the desktop value, and says so; anything else
 * stays a desktop setting. That is the whole responsive model an author has to learn: pick the
 * device, change what should differ.
 *
 * Text is committed on blur and Enter, not per keystroke: each commit is an undo step, and
 * undoing a heading one letter at a time is not undo anyone wants.
 */
export function PropsPanel({ editor, registry }: { editor: Editor | null; registry: Registry }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  const device = useVisual((s) => s.device);
  const catalog = useContentCatalog();
  // Undo/redo change props without changing the selection; re-read on those too.
  useEditorEvent(editor, `component:update:${PROPS} component:update:${EXTRA} undo redo`);

  const latest = selected ? registry.get(selected.get('type') ?? '') : undefined;
  const pinned = (selected?.get(EXTRA) as NodeExtra | undefined)?.version;
  // An instance pinned to an older version of a site component is edited as that version.
  const definition = latest && pinned !== undefined && pinned !== latest.version ? (latest.olderVersions?.[pinned] ?? latest) : latest;
  if (!selected || !definition) {
    return <p className="p-4 text-sm text-muted-foreground">{t('visual.selectSomething')}</p>;
  }

  const props = (selected.get(PROPS) ?? {}) as Record<string, unknown>;
  const extra = (selected.get(EXTRA) ?? {}) as NodeExtra;

  // The item this node can bind to: its collection's, or the detail page's (P4).
  const target = useVisual.getState().target;
  const pageSource = target?.kind === 'page' ? readPage(target.id)?.data?.source : undefined;
  const scope = scopeOf(selected, !!pageSource);
  const scopeSource: Source | null | undefined = scope?.kind === 'collection' ? scope.source : scope?.kind === 'page' ? pageSource : null;
  const fields: ContentField[] = scopeSource
    ? [...(catalog.sources.find((s) => sourceKey(s.source) === sourceKey(scopeSource))?.fields ?? []), ...META_BINDABLE]
    : [];
  const writeBind = (name: string, path: string | undefined) => {
    const bind = { ...extra.bind };
    if (path) bind[name] = path;
    else delete bind[name];
    const next: NodeExtra = { ...extra, bind: Object.keys(bind).length ? bind : undefined };
    if (!next.bind) delete next.bind;
    selected.set(EXTRA, next);
  };
  const overrides = device === 'desktop' ? undefined : extra.responsive?.[device];

  const writeProp = (prop: PropDefinition, value: unknown) => {
    if (device !== 'desktop' && prop.responsive) {
      const responsive: Responsive = { ...extra.responsive };
      const forDevice = { ...responsive[device] };
      if (value === undefined) delete forDevice[prop.name];
      else forDevice[prop.name] = value;
      if (Object.keys(forDevice).length) responsive[device] = forDevice;
      else delete responsive[device];
      const next: NodeExtra = { ...extra, responsive: Object.keys(responsive).length ? responsive : undefined };
      if (!next.responsive) delete next.responsive;
      selected.set(EXTRA, next);
      return;
    }
    const next = { ...props };
    if (value === undefined || value === '') delete next[prop.name];
    else next[prop.name] = value;
    selected.set(PROPS, next);
  };

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div className="border-b px-4 py-3">
        <div className="text-sm font-medium">{definition.label}</div>
        {definition.description && <p className="mt-0.5 text-xs text-muted-foreground">{definition.description}</p>}
        {device !== 'desktop' && (
          <p className="mt-2 rounded bg-primary/10 px-2 py-1 text-xs text-primary">
            {t('visual.editingDevice', { device: t(`visual.devices.${device}`) })}
          </p>
        )}
      </div>
      <div className="space-y-4 p-4">
        {latest && <InstanceVersion selected={selected} latest={latest} />}
        {definition.type.startsWith(CODE_PREFIX) && (
          <Button
            size="sm"
            variant="outline"
            className="w-full"
            onClick={() => {
              useVisual.getState().setView('split');
              useVfs.getState().open(codeSourcePath(definition.type.slice(CODE_PREFIX.length)));
            }}
          >
            <FileCode className="h-4 w-4" /> {t('visual.mine.editSource')}
          </Button>
        )}
        {definition.props.length === 0 && !definition.actions?.length && (
          <p className="text-sm text-muted-foreground">{t('visual.noProps')}</p>
        )}
        {definition.props.map((prop) => {
          const overridden = overrides?.[prop.name] !== undefined && prop.responsive;
          const value = overridden ? overrides![prop.name] : props[prop.name];
          return (
            <PropField
              key={`${selected.cid}:${device}:${prop.name}`}
              component={selected}
              prop={prop}
              value={value}
              sources={catalog.sources}
              binding={
                scope && device === 'desktop' && BINDABLE[prop.kind]
                  ? {
                      fields: fields.filter((f) => BINDABLE[prop.kind]!.includes(f.kind)),
                      path: extra.bind?.[prop.name],
                      unknownSource: !scopeSource,
                      onBind: (path) => writeBind(prop.name, path),
                    }
                  : undefined
              }
              deviceNote={
                device !== 'desktop'
                  ? prop.responsive
                    ? overridden
                      ? { kind: 'override', onReset: () => writeProp(prop, undefined) }
                      : { kind: 'inherits' }
                    : { kind: 'desktopOnly' }
                  : undefined
              }
              onCommit={(next) => writeProp(prop, next)}
            />
          );
        })}
        {definition.actions?.length ? (
          <ActionField
            key={`${selected.cid}:action`}
            editor={editor}
            inItem={!!scope}
            definition={definition}
            action={extra.action}
            onCommit={(action) => {
              const next: NodeExtra = { ...extra, action };
              if (!action) delete next.action;
              selected.set(EXTRA, next);
            }}
          />
        ) : null}
        <ExposePanel selected={selected} definition={definition} />
        {/* Out of the way on purpose: the design kit stays authoritative for everyone else. */}
        <details className="rounded-md border px-3 py-2" open={!!extra.css}>
          <summary className="cursor-pointer text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('visual.css.advanced')}</summary>
          <div className="mt-2 space-y-1">
            <Label htmlFor={`${selected.cid}:css`}>{t('visual.css.label')}</Label>
            <TextField
              key={`${selected.cid}:css`}
              id={`${selected.cid}:css`}
              multiline
              value={extra.css ?? ''}
              placeholder="letter-spacing: .1em; border-radius: 0"
              onCommit={(value) => {
                const css = value.trim();
                const checked = nodeCssSchema.safeParse(css);
                if (css && !checked.success) return void toast.error(checked.error.issues[0]?.message ?? t('visual.css.invalid'));
                const next: NodeExtra = { ...extra, css: css || undefined };
                if (!css) delete next.css;
                selected.set(EXTRA, next);
              }}
            />
            <p className="text-xs text-muted-foreground">{t('visual.css.hint')}</p>
          </div>
        </details>
      </div>
    </div>
  );
}

type DeviceNote = { kind: 'override'; onReset: () => void } | { kind: 'inherits' } | { kind: 'desktopOnly' };

interface Binding {
  fields: ContentField[];
  path?: string;
  /** In an item's scope, but the collection has no content chosen yet. */
  unknownSource: boolean;
  onBind: (path: string | undefined) => void;
}

function PropField({
  component,
  prop,
  value,
  deviceNote,
  binding,
  sources,
  onCommit,
}: {
  component: Component;
  prop: PropDefinition;
  value: unknown;
  deviceNote?: DeviceNote;
  binding?: Binding;
  sources: { source: Source; label: string }[];
  onCommit: (value: unknown) => void;
}) {
  const { t } = useTranslation();
  const id = `prop-${component.cid}-${prop.name}`;
  const [error, setError] = useState<string | null>(null);

  const commit = (next: unknown) => {
    if (next !== undefined && next !== '') {
      const parsed = propValueSchema(prop).safeParse(next);
      if (!parsed.success) {
        setError(parsed.error.issues[0]?.message ?? t('visual.invalidValue'));
        return;
      }
    }
    setError(null);
    if (next !== value) onCommit(next);
  };

  let control: React.ReactNode;
  switch (prop.kind) {
    case 'source': {
      const current = value && typeof value === 'object' ? sourceKey(value as Source) : '';
      control = (
        <Select value={current} onValueChange={(key) => commit(sources.find((s) => sourceKey(s.source) === key)?.source)}>
          <SelectTrigger id={id}>
            <SelectValue placeholder={t('visual.data.chooseSource')} />
          </SelectTrigger>
          <SelectContent>
            {sources.map((s) => (
              <SelectItem key={sourceKey(s.source)} value={sourceKey(s.source)}>
                {s.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      );
      break;
    }
    case 'select':
      control = (
        <Select value={typeof value === 'string' ? value : (prop.default ?? '')} onValueChange={commit}>
          <SelectTrigger id={id}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {prop.options.map((o) => (
              <SelectItem key={o.value} value={o.value}>
                {o.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      );
      break;
    case 'boolean':
      control = <Switch id={id} checked={value === true} onCheckedChange={(checked) => commit(checked)} />;
      break;
    case 'number':
      control = (
        <TextField
          id={id}
          type="number"
          value={typeof value === 'number' ? String(value) : ''}
          onCommit={(raw) => commit(raw === '' ? undefined : Number(raw))}
        />
      );
      break;
    case 'media': {
      // Stored as the published URL (/api/media/<id>/original), which the site resolves
      // same-origin; the canvas swaps in an authenticated copy (MediaBridge). See builder/panels/media.ts.
      const url = typeof value === 'string' ? value : '';
      control = (
        <div className="space-y-2">
          <MediaPicker
            value={assetIdFrom(url)}
            category={'Image' as never}
            onChange={(assetId) => commit(assetId ? mediaUrlFor(assetId) : undefined)}
          />
          <TextField id={id} value={url} placeholder={t('visual.mediaUrlPlaceholder')} onCommit={(raw) => commit(raw)} />
        </div>
      );
      break;
    }
    default:
      control = (
        <TextField
          id={id}
          multiline={prop.kind === 'richText' || (prop.kind === 'text' && prop.multiline === true)}
          value={typeof value === 'string' ? value : ''}
          onCommit={(raw) => commit(raw)}
        />
      );
  }

  return (
    <div className="space-y-1.5">
      <div className="flex items-center gap-2">
        <Label htmlFor={id} className="min-w-0 flex-1">
          {prop.label}
        </Label>
        {deviceNote?.kind === 'override' && (
          <button
            type="button"
            onClick={deviceNote.onReset}
            title={t('visual.resetOverride')}
            className="flex items-center gap-1 rounded px-1 text-[11px] text-primary hover:bg-primary/10"
          >
            <RotateCcw className="h-3 w-3" /> {t('visual.overridden')}
          </button>
        )}
        {deviceNote?.kind === 'inherits' && <span className="text-[11px] text-muted-foreground">{t('visual.inheritsDesktop')}</span>}
        {deviceNote?.kind === 'desktopOnly' && <span className="text-[11px] text-muted-foreground">{t('visual.allDevices')}</span>}
      </div>
      {binding && (
        <Select value={binding.path ?? '__fixed'} onValueChange={(v) => binding.onBind(v === '__fixed' ? undefined : v)}>
          <SelectTrigger className="h-7 text-xs" aria-label={t('visual.data.bindTo', { prop: prop.label })}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__fixed">{t('visual.data.fixedValue')}</SelectItem>
            {binding.path && !binding.fields.some((f) => f.path === binding.path) && (
              <SelectItem value={binding.path}>{binding.path}</SelectItem>
            )}
            {binding.fields.map((f) => (
              <SelectItem key={f.path} value={f.path}>
                {t('visual.data.showsField', { field: f.label })}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
      {binding?.path ? (
        <p className="rounded bg-primary/10 px-2 py-1 text-xs text-primary">
          {t('visual.data.boundHint', { field: binding.fields.find((f) => f.path === binding.path)?.label ?? binding.path })}
        </p>
      ) : (
        control
      )}
      {binding?.unknownSource && <p className="text-xs text-muted-foreground">{t('visual.data.noSourceYet')}</p>}
      {error ? (
        <p className="text-xs text-destructive" role="alert">
          {error}
        </p>
      ) : (
        prop.description && <p className="text-xs text-muted-foreground">{prop.description}</p>
      )}
    </div>
  );
}

/**
 * What the component does when clicked — a page on this site or a link elsewhere. Validated with
 * the same `actionSchema` the site loads with, so a `javascript:` link cannot be saved.
 */
function ActionField({
  editor,
  inItem,
  definition,
  action,
  onCommit,
}: {
  editor: Editor | null;
  /** Inside a collection item or on a detail page: links may use `:slug`. */
  inItem: boolean;
  definition: ComponentDefinition;
  action: Action | undefined;
  onCommit: (action: Action | undefined) => void;
}) {
  const { t } = useTranslation();
  const app = useVisual((s) => s.app);
  const [error, setError] = useState<string | null>(null);
  const kind = action?.type ?? 'none';
  const allowed = definition.actions ?? [];
  // Sections to scroll to, and popups to open: the nodes on the canvas, by their layer name.
  const nodes: { id: string; label: string; type: string }[] = [];
  const stack = [...(editor?.getWrapper()?.components().models ?? [])];
  while (stack.length) {
    const c = stack.shift()!;
    const id = c.get(ID) as string | undefined;
    if (id) nodes.push({ id, label: `${c.getName()} · ${id}`, type: c.get('type') ?? '' });
    stack.push(...c.components().models);
  }
  const modals = nodes.filter((n) => n.type === 'dcms.modal');

  const commit = (next: Action | undefined) => {
    if (next) {
      const parsed = actionSchema.safeParse(next);
      if (!parsed.success) {
        setError(parsed.error.issues[0]?.message ?? t('visual.invalidValue'));
        return;
      }
    }
    setError(null);
    onCommit(next);
  };

  return (
    <div className="space-y-2 border-t pt-4">
      <Label>{t('visual.action.title')}</Label>
      <Select
        value={kind}
        onValueChange={(v) =>
          commit(
            v === 'navigate'
              ? { type: 'navigate', to: app?.routes[0]?.path ?? '/' }
              : v === 'open-external'
                ? { type: 'open-external', href: 'https://' }
                : v === 'scroll-to'
                  ? { type: 'scroll-to', target: nodes[0]?.id ?? 'top' }
                  : v === 'open-modal'
                    ? { type: 'open-modal', modal: modals[0]?.id ?? 'popup' }
                    : v === 'show-toast'
                      ? { type: 'show-toast', message: t('visual.action.toastDefault') }
                      : undefined,
          )
        }
      >
        <SelectTrigger aria-label={t('visual.action.title')}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="none">{t('visual.action.none')}</SelectItem>
          {allowed.includes('navigate') && <SelectItem value="navigate">{t('visual.action.navigate')}</SelectItem>}
          {allowed.includes('open-external') && <SelectItem value="open-external">{t('visual.action.external')}</SelectItem>}
          {allowed.includes('scroll-to') && <SelectItem value="scroll-to">{t('visual.action.scrollTo')}</SelectItem>}
          {allowed.includes('open-modal') && <SelectItem value="open-modal">{t('visual.action.openModal')}</SelectItem>}
          {allowed.includes('show-toast') && <SelectItem value="show-toast">{t('visual.action.toast')}</SelectItem>}
        </SelectContent>
      </Select>

      {action?.type === 'navigate' && (
        <Select value={action.to} onValueChange={(to) => commit({ type: 'navigate', to })}>
          <SelectTrigger aria-label={t('visual.action.page')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {(app?.routes ?? [])
              // A detail page's address needs an item to fill `:slug` from.
              .filter((r) => inItem || !r.path.includes(':'))
              .map((r) => (
                <SelectItem key={r.id} value={r.path}>
                  {r.path}
                </SelectItem>
              ))}
          </SelectContent>
        </Select>
      )}
      {action?.type === 'open-external' && (
        <>
          <TextField
            id="action-href"
            value={action.href === 'https://' ? '' : action.href}
            placeholder="https://"
            onCommit={(href) => commit({ ...action, href })}
          />
          <label className="flex items-center gap-2 text-sm">
            <Switch checked={action.newTab === true} onCheckedChange={(newTab) => commit({ ...action, newTab })} />
            {t('visual.action.newTab')}
          </label>
        </>
      )}
      {action?.type === 'scroll-to' && (
        <Select value={action.target} onValueChange={(target) => commit({ type: 'scroll-to', target })}>
          <SelectTrigger aria-label={t('visual.action.section')}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {nodes.map((n) => (
              <SelectItem key={n.id} value={n.id}>
                {n.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
      {action?.type === 'open-modal' &&
        (modals.length ? (
          <Select value={action.modal} onValueChange={(modal) => commit({ type: 'open-modal', modal })}>
            <SelectTrigger aria-label={t('visual.action.popup')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {modals.map((n) => (
                <SelectItem key={n.id} value={n.id}>
                  {n.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        ) : (
          <p className="text-xs text-muted-foreground">{t('visual.action.noPopups')}</p>
        ))}
      {action?.type === 'show-toast' && (
        <TextField id="action-toast" value={action.message} onCommit={(message) => message && commit({ ...action, message })} />
      )}
      {error && (
        <p className="text-xs text-destructive" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

/** A text input holding a local draft, written back on blur or Enter (Ctrl+Enter when multiline). */
function TextField({
  id,
  value,
  onCommit,
  multiline,
  type,
  placeholder,
}: {
  id: string;
  value: string;
  onCommit: (value: string) => void;
  multiline?: boolean;
  type?: string;
  placeholder?: string;
}) {
  const [draft, setDraft] = useState(value);
  // An undo, or the AI, can change the value underneath an untouched field.
  useEffect(() => setDraft(value), [value]);

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (!multiline || e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      onCommit(draft);
    } else if (e.key === 'Escape') {
      setDraft(value);
    }
  };

  return multiline ? (
    <Textarea
      id={id}
      rows={4}
      value={draft}
      placeholder={placeholder}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => onCommit(draft)}
      onKeyDown={onKeyDown}
    />
  ) : (
    <Input
      id={id}
      type={type}
      value={draft}
      placeholder={placeholder}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => onCommit(draft)}
      onKeyDown={onKeyDown}
    />
  );
}
