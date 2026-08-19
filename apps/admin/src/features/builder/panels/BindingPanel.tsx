import { META_FIELDS, suggestedTarget, type ContentField } from '@dcms/gjs-blocks';
import {
  BIND_ATTR,
  BIND_TARGETS,
  EMPTY_ATTR,
  FALLBACK_ATTR,
  FORMAT_ATTR,
  IF_ATTR,
  PREFIX_ATTR,
  REPEAT_ATTR,
  SUFFIX_ATTR,
  TRUNCATE_ATTR,
  UNLESS_ATTR,
  parseBind,
  type ComponentDefinition,
} from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { Repeat } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Input } from '../../../components/ui/input';
import { cn } from '../../../lib/cn';
import { useContentFields } from '../plugins/fields';
import { useBuilder } from '../store';
import { useEditorEvent, useSelected } from './useEditorEvent';

/**
 * Where a tenant component's markup is wired to their data.
 *
 * This is the half of the component builder that the canvas cannot be: the
 * author lays the component out with ordinary blocks, and here they say *what
 * fills what*. Every control writes one attribute on the selected element, and
 * those attributes are the template — so what this panel produces is exactly
 * what the code view shows and what both renderers read. There is no second
 * representation of a binding anywhere.
 *
 * The field list is the tenant's own: the plugin's fixed fields plus whatever
 * that instance's configuration defines, at the path the value actually lives
 * at. That is the requirement this whole feature exists for — two tenants with
 * the same plugin have different fields, and a builder that only knew the
 * plugin's would be blind to most of what their API returns.
 */
export function BindingPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  useEditorEvent(editor, 'component:update trait:value');
  const definition = useBuilder((s) =>
    s.activeKind === 'component'
      ? (s.project?.components.find((c) => c.name === s.activeSlug) ?? null)
      : null,
  );
  const fields = useContentFields(definition?.source?.instanceSlug, definition?.source?.contentType);

  if (!definition) {
    return <p className="p-4 text-sm text-muted-foreground">{t('builder.components.notATemplate')}</p>;
  }
  if (!selected) {
    return <p className="p-4 text-sm text-muted-foreground">{t('builder.selectSomething')}</p>;
  }

  const attributes = selected.getAttributes() as Record<string, string>;
  const set = (name: string, value: string | null) => {
    if (value === null || value === '') selected.removeAttributes(name);
    else selected.addAttributes({ [name]: value });
    // The attribute is on the model, so the capture that follows writes it into
    // the template — but nothing else redraws the panel.
    editor?.trigger('component:update', selected);
  };

  const bind = parseBind(attributes[BIND_ATTR] ?? '');
  const sources = bindableSources(definition, fields, t);
  const repeating = attributes[REPEAT_ATTR] !== undefined;
  const isEmptyMarker = attributes[EMPTY_ATTR] !== undefined;

  const setSource = (source: string) => {
    if (!source) {
      set(BIND_ATTR, null);
      return;
    }
    // Changing the source re-suggests the target only when none was chosen yet;
    // an author who deliberately bound a field to `alt` should not have it
    // silently moved back to `text`.
    const target = bind?.target ?? suggestedTarget(kindOf(source, fields));
    set(BIND_ATTR, `${target}:${source}`);
  };

  return (
    <div className="space-y-4 p-3">
      <section className="space-y-2">
        <Header>{t('builder.components.element')}</Header>
        <p className="truncate text-sm">{selected.getName()}</p>

        {definition.source && (
          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              checked={repeating}
              onChange={(e) => set(REPEAT_ATTR, e.target.checked ? '' : null)}
              className="mt-0.5 h-4 w-4"
            />
            <span>
              <span className="flex items-center gap-1.5">
                <Repeat className="h-3.5 w-3.5" />
                {t('builder.components.repeat')}
              </span>
              <span className="block text-xs text-muted-foreground">
                {t('builder.components.repeatHint')}
              </span>
            </span>
          </label>
        )}
      </section>

      <section className="space-y-2 border-t pt-3">
        <Header>{t('builder.components.binding')}</Header>

        <Field label={t('builder.components.fills')}>
          <Select value={bind?.source ?? ''} onChange={setSource}>
            <option value="">{t('builder.components.nothing')}</option>
            {sources.map((group) => (
              <optgroup key={group.label} label={group.label}>
                {group.fields.map((field) => (
                  <option key={field.path} value={field.path}>
                    {field.label}
                  </option>
                ))}
              </optgroup>
            ))}
          </Select>
        </Field>

        {bind && (
          <>
            <Field label={t('builder.components.into')}>
              <Select
                value={bind.target}
                onChange={(target) => set(BIND_ATTR, `${target}:${bind.source}`)}
              >
                {BIND_TARGETS.map((target) => (
                  <option key={target} value={target}>
                    {t(`builder.components.target.${target.replace(':', '_')}`)}
                  </option>
                ))}
              </Select>
            </Field>

            <Field label={t('builder.components.format')}>
              <Select value={attributes[FORMAT_ATTR] ?? ''} onChange={(v) => set(FORMAT_ATTR, v)}>
                <option value="">{t('builder.components.formatAuto')}</option>
                <option value="date">{t('builder.components.formatDate')}</option>
                <option value="media">{t('builder.components.formatMedia')}</option>
                <option value="raw">{t('builder.components.formatRaw')}</option>
              </Select>
            </Field>

            <div className="grid grid-cols-2 gap-2">
              <Field label={t('builder.components.prefix')}>
                <Input
                  className="h-8"
                  value={attributes[PREFIX_ATTR] ?? ''}
                  placeholder="/blog/"
                  onChange={(e) => set(PREFIX_ATTR, e.target.value)}
                />
              </Field>
              <Field label={t('builder.components.suffix')}>
                <Input
                  className="h-8"
                  value={attributes[SUFFIX_ATTR] ?? ''}
                  onChange={(e) => set(SUFFIX_ATTR, e.target.value)}
                />
              </Field>
            </div>

            <div className="grid grid-cols-2 gap-2">
              <Field label={t('builder.components.truncate')}>
                <Input
                  className="h-8"
                  type="number"
                  min={0}
                  value={attributes[TRUNCATE_ATTR] ?? ''}
                  onChange={(e) => set(TRUNCATE_ATTR, e.target.value)}
                />
              </Field>
              <Field label={t('builder.components.fallback')}>
                <Input
                  className="h-8"
                  value={attributes[FALLBACK_ATTR] ?? ''}
                  onChange={(e) => set(FALLBACK_ATTR, e.target.value)}
                />
              </Field>
            </div>
          </>
        )}
      </section>

      <section className="space-y-2 border-t pt-3">
        <Header>{t('builder.components.visibility')}</Header>
        <p className="text-xs text-muted-foreground">{t('builder.components.visibilityHint')}</p>

        <Field label={t('builder.components.showIf')}>
          <Select value={attributes[IF_ATTR] ?? ''} onChange={(v) => set(IF_ATTR, v)}>
            <option value="">{t('builder.components.always')}</option>
            {sources.map((group) => (
              <optgroup key={group.label} label={group.label}>
                {group.fields.map((field) => (
                  <option key={field.path} value={field.path}>
                    {field.label}
                  </option>
                ))}
              </optgroup>
            ))}
          </Select>
        </Field>

        <Field label={t('builder.components.hideIf')}>
          <Select value={attributes[UNLESS_ATTR] ?? ''} onChange={(v) => set(UNLESS_ATTR, v)}>
            <option value="">{t('builder.components.never')}</option>
            {sources.map((group) => (
              <optgroup key={group.label} label={group.label}>
                {group.fields.map((field) => (
                  <option key={field.path} value={field.path}>
                    {field.label}
                  </option>
                ))}
              </optgroup>
            ))}
          </Select>
        </Field>

        {definition.source && (
          <label className="flex items-start gap-2 text-sm">
            <input
              type="checkbox"
              checked={isEmptyMarker}
              onChange={(e) => set(EMPTY_ATTR, e.target.checked ? '' : null)}
              className="mt-0.5 h-4 w-4"
            />
            <span>
              {t('builder.components.emptyState')}
              <span className="block text-xs text-muted-foreground">
                {t('builder.components.emptyStateHint')}
              </span>
            </span>
          </label>
        )}
      </section>
    </div>
  );
}

/** Everything the selected element can be bound to, grouped as an author sees it. */
function bindableSources(
  definition: ComponentDefinition,
  fields: readonly ContentField[],
  t: (key: string) => string,
): { label: string; fields: ContentField[] }[] {
  const groups: { label: string; fields: ContentField[] }[] = [];

  const plugin = fields.filter((f) => !f.custom);
  const custom = fields.filter((f) => f.custom);
  if (plugin.length) groups.push({ label: t('builder.components.groupFields'), fields: plugin });
  // Kept as its own group: these are the fields this tenant defined, and burying
  // them among the plugin's would hide the ones they actually named.
  if (custom.length) groups.push({ label: t('builder.components.groupCustom'), fields: custom });
  if (definition.source) groups.push({ label: t('builder.components.groupItem'), fields: META_FIELDS });

  if (definition.props.length) {
    groups.push({
      label: t('builder.components.groupProps'),
      fields: definition.props.map((prop) => ({
        path: `@${prop.name}`,
        label: prop.label,
        kind: prop.kind === 'media' ? 'media' : 'text',
        custom: false,
      })),
    });
  }
  return groups;
}

function kindOf(source: string, fields: readonly ContentField[]): ContentField['kind'] {
  return fields.find((f) => f.path === source)?.kind ?? 'text';
}

function Header({ children }: { children: React.ReactNode }) {
  return (
    <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{children}</h3>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block space-y-1">
      <span className="text-xs font-medium">{label}</span>
      {children}
    </label>
  );
}

function Select({
  value,
  onChange,
  children,
  className,
}: {
  value: string;
  onChange: (value: string) => void;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className={cn('h-8 w-full rounded-md border bg-background px-2 text-sm', className)}
    >
      {children}
    </select>
  );
}
