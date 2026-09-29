import type { RJSFSchema, UiSchema } from '@rjsf/utils';

/**
 * Formats that name a custom widget rather than a string constraint. A field whose
 * `format` appears here is routed to the widget of the same name, which the caller
 * supplies via `widgets`.
 *
 * A list rather than a growing chain of ifs because these arrive one per feature —
 * `media` for Branding's logo, `meta-connection` for the social plugins' account —
 * and each one was previously a second edit in a second file that was easy to miss.
 * A format with no matching widget simply renders as a plain input.
 */
const WIDGET_FORMATS = ['media', 'meta-connection'] as const;

/** Schema annotation marking a field that binds a contract provider: `"x-dcms-contract-binding": "roster.members@1"`. */
export const CONTRACT_BINDING_KEY = 'x-dcms-contract-binding';

/**
 * Walks a schema and emits a uiSchema that routes any field carrying one of
 * {@link WIDGET_FORMATS} to the widget of that name. A plugin's config schema is the
 * only thing the manifest ships — there is no hand-authored uiSchema — so the widget
 * assignment is derived from the schema.
 */
export function buildUiSchema(schema: RJSFSchema): UiSchema {
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

  if (schema.format && (WIDGET_FORMATS as readonly string[]).includes(schema.format)) {
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
