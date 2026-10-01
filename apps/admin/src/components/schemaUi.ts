import type { RJSFSchema, UiSchema } from '@rjsf/utils';

/*
 * A field whose `format` names a registered widget is routed to that widget. The set is the
 * form's widgets, not a list kept here: the console registers its own (`media`, `json`) and a
 * plugin's admin UI adds its own (`meta-connection`, docs/adr/0019), and a list in this file
 * was a second edit nobody could make for an installed plugin. A format with no widget renders
 * as a plain input.
 */

/** Schema annotation marking a field that binds a contract provider: `"x-dcms-contract-binding": "roster.members@1"`. */
export const CONTRACT_BINDING_KEY = 'x-dcms-contract-binding';

/**
 * Walks a schema and emits a uiSchema that routes any field carrying one of
 * `widgetFormats` to the widget of that name. A plugin's config schema is the
 * only thing the manifest ships — there is no hand-authored uiSchema — so the widget
 * assignment is derived from the schema.
 */
export function buildUiSchema(schema: RJSFSchema, widgetFormats: readonly string[] = []): UiSchema {
  const ui: UiSchema = {};

  if (schema.type === 'object' && schema.properties) {
    for (const [key, child] of Object.entries(schema.properties)) {
      if (typeof child === 'object') {
        const childUi = buildUiSchema(child as RJSFSchema, widgetFormats);
        if (Object.keys(childUi).length > 0) ui[key] = childUi;
      }
    }
  } else if (schema.type === 'array' && schema.items && typeof schema.items === 'object') {
    const itemUi = buildUiSchema(schema.items as RJSFSchema, widgetFormats);
    if (Object.keys(itemUi).length > 0) ui.items = itemUi;
  }

  if (schema.format && widgetFormats.includes(schema.format)) {
    ui['ui:widget'] = schema.format;
  }

  // A contract binding (docs/adr/0016): the field names which instance of another plugin this
  // one uses. Routed to the `contract-binding` widget, told which contract to list providers of.
  const binding = (schema as Record<string, unknown>)[CONTRACT_BINDING_KEY];
  if (typeof binding === 'string' && binding.length > 0) {
    ui['ui:widget'] = 'contract-binding';
    ui['ui:options'] = { contract: binding };
  }

  return ui;
}
