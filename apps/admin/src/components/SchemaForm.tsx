import Form from '@rjsf/core';
import type { IconButtonProps, RJSFSchema, RegistryWidgetsType, UiSchema } from '@rjsf/utils';
import validator from '@rjsf/validator-ajv8';
import { ArrowDown, ArrowUp, Copy, Plus, X } from 'lucide-react';
import { Button } from './ui/button';

/**
 * RJSF's stock buttons render their only content as `<i class="glyphicon ...">`,
 * a Bootstrap icon font this app does not load — so out of the box the add,
 * remove and reorder controls are invisible empty boxes. These templates replace
 * them with the icon set the rest of the admin uses, and give them real
 * accessible names instead of relying on `title`.
 */
function toolbarButton(icon: React.ReactNode, label: string) {
  return function ToolbarButton({
    icon: _icon,
    iconType: _iconType,
    uiSchema: _uiSchema,
    registry: _registry,
    // RJSF inlines `flex:1` and bold text on toolbar buttons to stretch them
    // across a Bootstrap column; dropped so they keep their square icon size.
    style: _style,
    className,
    ...props
  }: IconButtonProps) {
    return (
      <Button
        type="button"
        variant="outline"
        size="icon"
        aria-label={label}
        title={label}
        className={cnMerge('h-8 w-8 shrink-0', className)}
        {...props}
      >
        {icon}
      </Button>
    );
  };
}

/** RJSF passes Bootstrap grid classes we do not want; keep only our own. */
function cnMerge(ours: string, theirs?: string) {
  const keep = (theirs ?? '')
    .split(' ')
    .filter((c) => c && !c.startsWith('col-') && !c.startsWith('btn'))
    .join(' ');
  return keep ? `${ours} ${keep}` : ours;
}

const AddButton = ({
  icon: _icon,
  iconType: _iconType,
  uiSchema: _uiSchema,
  registry: _registry,
  className,
  ...props
}: IconButtonProps) => (
  <Button
    type="button"
    variant="outline"
    size="sm"
    className={cnMerge('mt-2', className)}
    {...props}
  >
    <Plus className="h-4 w-4" />
    Add item
  </Button>
);

const templates = {
  ButtonTemplates: {
    AddButton,
    RemoveButton: toolbarButton(<X className="h-4 w-4" />, 'Remove item'),
    MoveUpButton: toolbarButton(<ArrowUp className="h-4 w-4" />, 'Move item up'),
    MoveDownButton: toolbarButton(<ArrowDown className="h-4 w-4" />, 'Move item down'),
    CopyButton: toolbarButton(<Copy className="h-4 w-4" />, 'Copy item'),
  },
};

/**
 * Walks a schema and emits a uiSchema that routes any string field carrying
 * `"format": "media"` to the `media` widget (supplied by the caller via `widgets`).
 * A plugin's config schema is the only thing the manifest ships — there is no
 * hand-authored uiSchema — so the widget assignment is derived from the schema.
 */
function buildUiSchema(schema: RJSFSchema): UiSchema {
  const ui: UiSchema = {};

  if (schema.type === 'object' && schema.properties) {
    for (const [key, child] of Object.entries(schema.properties)) {
      if (typeof child === 'object') {
        const childUi = buildUiSchema(child as RJSFSchema);
        if (Object.keys(childUi).length > 0) ui[key] = childUi;
      }
    }
  } else if (schema.type === 'array' && schema.items && typeof schema.items === 'object') {
    const itemUi = buildUiSchema(schema.items as RJSFSchema);
    if (Object.keys(itemUi).length > 0) ui.items = itemUi;
  }

  if (schema.format === 'media') ui['ui:widget'] = 'media';

  return ui;
}

/**
 * JSON-Schema-driven form (react-jsonschema-form). Controlled via formData/onChange;
 * the default submit button is suppressed — callers provide their own save action.
 * Styled through the `.dcms-rjsf` scope in index.css.
 *
 * `widgets` lets a caller register custom RJSF widgets (e.g. a media picker for
 * fields declared with `"format": "media"`); the matching uiSchema is derived
 * automatically from the schema.
 */
export function SchemaForm({
  schema,
  formData,
  onChange,
  widgets,
}: {
  schema: RJSFSchema;
  formData: unknown;
  onChange: (data: unknown) => void;
  widgets?: RegistryWidgetsType;
}) {
  return (
    <div className="dcms-rjsf">
      <Form
        schema={schema}
        uiSchema={buildUiSchema(schema)}
        formData={formData}
        validator={validator}
        templates={templates}
        widgets={widgets}
        onChange={(e) => onChange(e.formData)}
        liveValidate={false}
        showErrorList={false}
      >
        <></>
      </Form>
    </div>
  );
}
