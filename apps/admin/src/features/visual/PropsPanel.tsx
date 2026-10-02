import { propValueSchema, type PropDefinition, type Registry } from '@dcms/site-runtime';
import type { Component, Editor } from 'grapesjs';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
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
import { useEditorEvent, useSelected } from '../builder/panels/useEditorEvent';
import { PROPS } from './canvas/tree';

/**
 * The inspector: the selected component's props, one control per declared prop.
 *
 * The controls come from the component's definition, so a prop added to a component appears
 * here with no inspector code. Every value is checked against the prop's own schema
 * (`propValueSchema`) before it is written — the same rule the validator and the AI tools
 * apply — so what can be typed here is exactly what can be saved.
 *
 * Text is committed on blur and Enter, not per keystroke: each commit is an undo step, and
 * undoing a heading one letter at a time is not undo anyone wants.
 */
export function PropsPanel({ editor, registry }: { editor: Editor | null; registry: Registry }) {
  const { t } = useTranslation();
  const selected = useSelected(editor);
  // Undo/redo change props without changing the selection; re-read on those too.
  useEditorEvent(editor, `component:update:${PROPS} undo redo`);

  const definition = selected ? registry.get(selected.get('type') ?? '') : undefined;
  if (!selected || !definition) {
    return <p className="p-4 text-sm text-muted-foreground">{t('visual.selectSomething')}</p>;
  }

  const props = (selected.get(PROPS) ?? {}) as Record<string, unknown>;
  const write = (name: string, value: unknown) => {
    const next = { ...props };
    if (value === undefined || value === '') delete next[name];
    else next[name] = value;
    selected.set(PROPS, next);
  };

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div className="border-b px-4 py-3">
        <div className="text-sm font-medium">{definition.label}</div>
        {definition.description && <p className="mt-0.5 text-xs text-muted-foreground">{definition.description}</p>}
      </div>
      <div className="space-y-4 p-4">
        {definition.props.length === 0 && <p className="text-sm text-muted-foreground">{t('visual.noProps')}</p>}
        {definition.props.map((prop) => (
          <PropField
            key={`${selected.cid}:${prop.name}`}
            component={selected}
            prop={prop}
            value={props[prop.name]}
            onCommit={(value) => write(prop.name, value)}
          />
        ))}
      </div>
    </div>
  );
}

function PropField({
  component,
  prop,
  value,
  onCommit,
}: {
  component: Component;
  prop: PropDefinition;
  value: unknown;
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
    default:
      control = (
        <TextField
          id={id}
          multiline={prop.kind === 'richText' || (prop.kind === 'text' && prop.multiline === true)}
          value={typeof value === 'string' ? value : ''}
          placeholder={prop.kind === 'media' ? t('visual.mediaUrlPlaceholder') : undefined}
          onCommit={(raw) => commit(raw)}
        />
      );
  }

  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{prop.label}</Label>
      {control}
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
