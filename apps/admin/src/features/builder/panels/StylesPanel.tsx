import type { Editor } from 'grapesjs';
import { ChevronDown, ChevronRight, RotateCcw } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { cn, Input } from '@dcms/admin-ui';
import { useEditorEvent, useSelected } from './useEditorEvent';

/**
 * The style editor.
 *
 * GrapesJS owns the sector/property model and the CSS rule the value lands in
 * (which is what keeps a style change attributable to the right stylesheet — see
 * the storage bridge); this panel only renders that model. Values are read fresh
 * on every render rather than mirrored into React state, so a style set from the
 * canvas, the code view or another panel is never shown stale here.
 */

interface PropertyOption {
  id?: string;
  value?: string;
  label?: string;
  name?: string;
}

interface PropertyModel {
  getId: () => string;
  getName: () => string;
  getLabel: () => string;
  getType: () => string;
  getValue: (opts?: object) => string;
  hasValue: (opts?: object) => boolean;
  upValue: (value: string, opts?: object) => void;
  clear: () => void;
  get: (key: string) => unknown;
  /** Only select-type properties implement this. */
  getOptions?: () => PropertyOption[];
}

/**
 * The presets a property offers.
 *
 * `getOptions()` exists only on GrapesJS's *select* property class, so a colour
 * property never exposes the theme palette through it — reading only that method
 * is why the swatches, once attached, still never appeared. The raw attribute is
 * where `applyThemeSwatches` puts them, and it is set on every property type.
 */
function optionsOf(property: PropertyModel): PropertyOption[] {
  const fromMethod = property.getOptions?.();
  if (fromMethod?.length) return fromMethod;
  const raw = property.get('options');
  return Array.isArray(raw) ? (raw as PropertyOption[]) : [];
}

interface SectorModel {
  getId: () => string;
  getName: () => string;
  isOpen: () => boolean;
  getProperties: () => PropertyModel[];
}

export function StylesPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  // Style edits, class changes and target switches all change what is shown.
  useEditorEvent(editor, 'component:styleUpdate component:update:classes style:target styleable:change update');
  // The user's explicit choice per sector, overriding the sector's own default.
  // This has to record *both* directions: a set of "closed" ids can never open a
  // sector that starts closed, which silently made Size, Typography, Decoration
  // and Effects unopenable — half the style panel, permanently out of reach.
  const [openOverrides, setOpenOverrides] = useState<Record<string, boolean>>({});

  if (!editor) return null;
  if (!selected) {
    return (
      <p className="p-4 text-sm text-muted-foreground">{t('builder.selectSomething')}</p>
    );
  }

  const sectors = editor.StyleManager.getSectors({ visible: true }) as unknown as SectorModel[];

  return (
    <div className="divide-y">
      {sectors.map((sector) => {
        const id = sector.getId();
        const open = openOverrides[id] ?? sector.isOpen();
        const properties = sector.getProperties();
        if (properties.length === 0) return null;

        return (
          <section key={id}>
            <button
              type="button"
              className="flex w-full items-center gap-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted-foreground"
              onClick={() => setOpenOverrides((prev) => ({ ...prev, [id]: !open }))}
              aria-expanded={open}
            >
              {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
              {sector.getName()}
            </button>

            {open && (
              <div className="space-y-2 px-3 pb-3">
                {properties.map((property) => (
                  <PropertyRow key={property.getId()} property={property} />
                ))}
              </div>
            )}
          </section>
        );
      })}
    </div>
  );
}

function PropertyRow({ property }: { property: PropertyModel }) {
  const { t } = useTranslation();
  const value = property.getValue();
  const set = property.hasValue({ noParent: true });
  const type = property.getType();
  const options = optionsOf(property);

  return (
    <label className="flex items-center gap-2 text-sm">
      <span
        className={cn(
          'w-28 shrink-0 truncate text-xs',
          // A property with its own value here (rather than inherited) is what
          // the author actually changed; making that visible is the difference
          // between editing a style and guessing at one.
          set ? 'font-medium text-foreground' : 'text-muted-foreground',
        )}
        title={property.getName()}
      >
        {property.getLabel()}
      </span>

      {type === 'select' && options.length > 0 ? (
        <select
          value={value}
          onChange={(e) => property.upValue(e.target.value)}
          className="h-8 min-w-0 flex-1 rounded-md border bg-background px-2 text-sm"
        >
          <option value="" />
          {options.map((option) => {
            const id = String(option.id ?? option.value ?? '');
            return (
              <option key={id} value={id}>
                {option.label ?? option.name ?? id}
              </option>
            );
          })}
        </select>
      ) : type === 'color' ? (
        <span className="flex min-w-0 flex-1 flex-col gap-1">
          <span className="flex min-w-0 items-center gap-1">
            <input
              type="color"
              value={/^#[0-9a-f]{6}$/i.test(value) ? value : '#000000'}
              onChange={(e) => property.upValue(e.target.value)}
              className="h-8 w-8 shrink-0 cursor-pointer rounded border bg-background p-0.5"
            />
            {/* The text field stays authoritative so `var(--dcms-color-brand)` can
                be typed — the native picker cannot express a token. */}
            <Input
              value={value}
              onChange={(e) => property.upValue(e.target.value)}
              className="h-8 min-w-0 flex-1"
            />
          </span>

          {/* The site's own palette, as one-click presets. Without these the
              theme is only reachable by typing `var(--dcms-color-…)` from
              memory, so in practice every colour ends up a hard-coded hex — the
              exact outcome having a theme is supposed to prevent. */}
          {options.length > 0 && (
            <span className="flex flex-wrap gap-1">
              {options.map((option) => {
                const token = String(option.id ?? option.value ?? '');
                return (
                  <button
                    key={token}
                    type="button"
                    title={`${option.label ?? token} — ${token}`}
                    aria-label={option.label ?? token}
                    onClick={() => property.upValue(token)}
                    style={{ background: option.value ?? token }}
                    className={cn(
                      'h-5 w-5 rounded border',
                      value === token ? 'ring-2 ring-ring ring-offset-1' : '',
                    )}
                  />
                );
              })}
            </span>
          )}
        </span>
      ) : (
        <Input
          value={value}
          placeholder={t('common.optional')}
          onChange={(e) => property.upValue(e.target.value)}
          className="h-8 min-w-0 flex-1"
        />
      )}

      <button
        type="button"
        title={t('builder.clearStyle')}
        onClick={() => property.clear()}
        className={cn(
          'shrink-0 rounded p-1 text-muted-foreground hover:text-foreground',
          set ? 'opacity-100' : 'opacity-0',
        )}
      >
        <RotateCcw className="h-3.5 w-3.5" />
      </button>
    </label>
  );
}
