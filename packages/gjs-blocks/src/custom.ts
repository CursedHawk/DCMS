import {
  specForComponent,
  type ComponentDefinition,
  type DcmsComponentSpec,
} from '@dcms/gjs-schema';
import { expandSnippet } from './render';

/**
 * Tenant components, as the builder registers them.
 *
 * `specForComponent` in `@dcms/gjs-schema` does the framework-free half — the
 * traits, the identity class, the placeholder binding — and stops where it needs
 * a DOM. This is that last step: a component with no data source drops as real
 * markup, so its template's bindings have to be resolved *once*, here, against
 * the prop defaults. Dropping the raw template instead would commit
 * `data-dcms-bind` attributes into the page, where nothing would ever read them
 * and the author would see an empty heading they could not explain.
 */
export function specsForComponents(
  definitions: readonly ComponentDefinition[],
  doc: Document,
): DcmsComponentSpec[] {
  return definitions.map((definition) => {
    const spec = specForComponent(definition);
    if (definition.source) return spec;
    return { ...spec, snippet: expandSnippet(doc, definition.template, defaultProps(definition)) };
  });
}

/** A definition's props at their declared defaults. */
export function defaultProps(definition: ComponentDefinition): Record<string, unknown> {
  const props: Record<string, unknown> = {};
  for (const prop of definition.props) {
    if (prop.default !== undefined) props[prop.name] = prop.default;
  }
  return props;
}
