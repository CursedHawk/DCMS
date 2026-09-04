import type { Editor } from 'grapesjs';
import { X } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Textarea } from '@dcms/admin-ui';
import { MediaPicker } from '../../media/MediaPicker';
import { useEditorEvent, useSelected } from './useEditorEvent';

/**
 * The settings inspector for the selected component.
 *
 * Traits come from the component's spec (see `@dcms/gjs-blocks`), so this panel
 * renders whatever the block declared without knowing anything about specific
 * components. The kinds GrapesJS has no built-in for are the point of running
 * the Trait Manager in `custom: true` mode: a `media` trait opens the real DCMS
 * media library instead of asking the author to paste a URL.
 */

interface TraitModel {
  getId: () => string;
  getName: () => string;
  getLabel: () => string;
  getType: () => string;
  getValue: () => unknown;
  setValue: (value: unknown) => void;
  get: (key: string) => unknown;
  getOptions?: () => { id?: string; value?: string; label?: string; name?: string }[];
}

interface TraitMeta {
  kind?: string;
  description?: string;
  accepts?: { mediaCategory?: string; pluginId?: string; contentType?: string };
}

export function TraitsPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  useEditorEvent(editor, 'trait:custom trait:value component:update');

  if (!editor) return null;
  if (!selected) {
    return <p className="p-4 text-sm text-muted-foreground">{t('builder.selectSomething')}</p>;
  }

  const traits = selected.getTraits() as unknown as TraitModel[];

  return (
    <div className="space-y-3 p-3">
      <div>
        <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
          {t('builder.component')}
        </p>
        <p className="truncate text-sm">{selected.getName()}</p>
      </div>

      {traits.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('builder.noSettings')}</p>
      ) : (
        traits.map((trait) => <TraitRow key={trait.getId()} trait={trait} />)
      )}

      <ClassEditor editor={editor} />
    </div>
  );
}

function TraitRow({ trait }: { trait: TraitModel }) {
  const { t } = useTranslation();
  const meta = (trait.get('dcms') ?? {}) as TraitMeta;
  const kind = meta.kind ?? trait.getType();
  const value = trait.getValue();
  const label = trait.getLabel() || trait.getName();

  const field = (() => {
    switch (kind) {
      case 'checkbox':
        return (
          <input
            type="checkbox"
            checked={value === true || value === 'true'}
            onChange={(e) => trait.setValue(e.target.checked)}
            className="h-4 w-4"
          />
        );
      case 'select': {
        const options = trait.getOptions?.() ?? [];
        return (
          <select
            value={String(value ?? '')}
            onChange={(e) => trait.setValue(e.target.value)}
            className="h-8 w-full rounded-md border bg-background px-2 text-sm"
          >
            {options.map((option) => {
              const id = String(option.id ?? option.value ?? '');
              return (
                <option key={id} value={id}>
                  {option.label ?? option.name ?? id}
                </option>
              );
            })}
          </select>
        );
      }
      case 'longText':
      case 'textarea':
        return (
          <Textarea rows={4} value={String(value ?? '')} onChange={(e) => trait.setValue(e.target.value)} />
        );
      case 'number':
        return (
          <Input
            type="number"
            value={String(value ?? '')}
            onChange={(e) => trait.setValue(e.target.value)}
            className="h-8"
          />
        );
      case 'date':
        return (
          <Input
            type="datetime-local"
            value={String(value ?? '')}
            onChange={(e) => trait.setValue(e.target.value)}
            className="h-8"
          />
        );
      case 'media':
        return <MediaTrait trait={trait} category={meta.accepts?.mediaCategory} />;
      default:
        return (
          <Input value={String(value ?? '')} onChange={(e) => trait.setValue(e.target.value)} className="h-8" />
        );
    }
  })();

  return (
    <label className="block space-y-1">
      <span className="text-xs font-medium">{label}</span>
      {field}
      {meta.description && <span className="block text-xs text-muted-foreground">{meta.description}</span>}
      {kind === 'contentRef' && (
        <span className="block text-xs text-muted-foreground">{t('builder.contentRefHint')}</span>
      )}
    </label>
  );
}

/** `/api/media/{id}/original` — the delivery URL the published page resolves. */
const MEDIA_URL = /^\/api\/media\/([0-9a-f-]{36})\/original$/i;

export function mediaUrlFor(assetId: string): string {
  return `/api/media/${assetId}/original`;
}

export function assetIdFrom(url: string): string | undefined {
  return MEDIA_URL.exec(url)?.[1];
}

/**
 * A media trait stores the delivery URL rather than a bare asset id, because the
 * markup in the repo *is* the published artifact — an editor-only reference
 * would have to be translated at build time, which is exactly the indirection
 * this format exists to avoid. The picker is the platform's own, so the trait
 * offers the real library rather than a URL box.
 *
 * The text field stays editable so an external URL still works.
 */
function MediaTrait({ trait, category }: { trait: TraitModel; category?: string }) {
  const { t } = useTranslation();
  const value = String(trait.getValue() ?? '');
  const assetId = assetIdFrom(value);

  return (
    <div className="space-y-2">
      <MediaPicker
        value={assetId}
        category={category as never}
        onChange={(id) => trait.setValue(id ? mediaUrlFor(id) : '')}
      />
      <div className="flex items-center gap-1">
        <Input
          value={value}
          placeholder={t('builder.noAsset')}
          onChange={(e) => trait.setValue(e.target.value)}
          className="h-8 min-w-0 flex-1"
        />
        {value && (
          <Button
            size="icon"
            variant="ghost"
            className="h-8 w-8 shrink-0"
            onClick={() => trait.setValue('')}
            title={t('actions.remove')}
          >
            <X className="h-4 w-4" />
          </Button>
        )}
      </div>
    </div>
  );
}

/**
 * The selected component's classes.
 *
 * Classes are how styles are shared across a site, so making them directly
 * editable here is what lets an author say "this is another card" rather than
 * restyling one element in isolation.
 */
function ClassEditor({ editor }: { editor: Editor }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  useEditorEvent(editor, 'component:update:classes');
  const [draft, setDraft] = useState('');

  if (!selected) return null;
  const classes = selected.getClasses();

  return (
    <div className="space-y-1 border-t pt-3">
      <span className="text-xs font-medium">{t('builder.classes')}</span>
      <div className="flex flex-wrap gap-1">
        {classes.map((name: string) => (
          <span key={name} className="flex items-center gap-1 rounded bg-muted px-1.5 py-0.5 text-xs">
            {name}
            <button
              type="button"
              onClick={() => selected.removeClass(name)}
              className="text-muted-foreground hover:text-destructive"
              aria-label={t('actions.remove')}
            >
              <X className="h-3 w-3" />
            </button>
          </span>
        ))}
      </div>
      <Input
        value={draft}
        placeholder={t('builder.addClass')}
        className="h-8"
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key !== 'Enter') return;
          const name = draft.trim().replace(/^\./, '');
          if (name) selected.addClass(name);
          setDraft('');
        }}
      />
    </div>
  );
}
