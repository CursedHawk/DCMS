import {
  decodePlaceholder,
  defaultProps,
  defaultQuery,
  encodePlaceholder,
  type DataBinding,
  type DcmsComponentSpec,
  type TraitSpec,
} from '@dcms/gjs-schema';

/**
 * Two-way binding between a plugin component's traits and its placeholder
 * attributes.
 *
 * A plugin component has no editable markup — what it publishes is
 * `data-dcms-component` / `-props` / `-bindings`, and hydrate.js turns that into
 * content at run time. So a trait cannot be a plain attribute: "page size" has to
 * end up inside the binding's query, "heading" inside the props object.
 *
 * GrapesJS gives us `changeProp` traits, which write to a model property and
 * stop there. This module is the missing half: it seeds those properties from the
 * markup when a component is created or parsed, and writes them back into the
 * placeholder whenever one changes. Without it a plugin block's settings panel
 * would appear to work and silently save nothing.
 */

/** The minimum of a GrapesJS component this module needs. Keeps it testable. */
export interface PlaceholderHost {
  get(key: string): unknown;
  set(key: string, value: unknown, options?: { silent?: boolean }): unknown;
  getAttributes(): Record<string, string>;
  addAttributes(attributes: Record<string, string>): unknown;
  removeAttributes(names: string | string[]): unknown;
  on(events: string, handler: () => void): unknown;
}

const OPTIONAL_ATTRS = ['data-dcms-props', 'data-dcms-bindings'];

function targeted(spec: DcmsComponentSpec, target: TraitSpec['target']): TraitSpec[] {
  return spec.traits.filter((t) => t.target === target);
}

/** Coerce a trait input value back to the type the placeholder JSON should hold. */
function coerce(trait: TraitSpec, value: unknown): unknown {
  if (value === '' || value === undefined || value === null) return undefined;
  if (trait.kind === 'number') {
    const n = Number(value);
    return Number.isFinite(n) ? n : undefined;
  }
  if (trait.kind === 'checkbox') return value === true || value === 'true';
  if (trait.kind === 'tags' && typeof value === 'string') {
    const items = value.split(',').map((v) => v.trim()).filter(Boolean);
    return items.length ? items : undefined;
  }
  return value;
}

/**
 * Seed the model's properties from the markup, so the inspector shows what the
 * page actually says rather than the spec's defaults.
 */
export function readPlaceholder(host: PlaceholderHost, spec: DcmsComponentSpec): void {
  const decoded = decodePlaceholder(host.getAttributes());
  const props = { ...defaultProps(spec), ...(decoded?.props ?? {}) };
  const query = { ...defaultQuery(spec), ...(decoded?.bindings[0]?.query ?? {}) };

  for (const trait of targeted(spec, 'prop')) {
    if (props[trait.name] !== undefined) host.set(trait.name, props[trait.name], { silent: true });
  }
  for (const trait of targeted(spec, 'query')) {
    if (query[trait.name] !== undefined) host.set(trait.name, query[trait.name], { silent: true });
  }
}

/**
 * Copy the model's values for `traits` into `target`.
 *
 * A property the model has never held is skipped rather than deleted: only the
 * author clearing a field (which leaves an empty value on the model) should
 * remove it from the markup. Without that distinction, writing a component the
 * author has not touched would strip the defaults it was created with.
 */
function applyTraits(
  host: PlaceholderHost,
  traits: readonly TraitSpec[],
  target: Record<string, unknown>,
): void {
  for (const trait of traits) {
    const raw = host.get(trait.name);
    if (raw === undefined) continue;
    const value = coerce(trait, raw);
    if (value === undefined) delete target[trait.name];
    else target[trait.name] = value;
  }
}

/** Write the model's trait properties back into the placeholder attributes. */
export function writePlaceholder(host: PlaceholderHost, spec: DcmsComponentSpec): void {
  const existing = decodePlaceholder(host.getAttributes());

  const props: Record<string, unknown> = { ...(existing?.props ?? {}) };
  applyTraits(host, targeted(spec, 'prop'), props);

  const binding = bindingOf(spec, existing?.bindings[0]);
  if (binding) applyTraits(host, targeted(spec, 'query'), binding.query);

  const attrs = encodePlaceholder({
    component: spec.type,
    props,
    bindings: binding ? [binding] : [],
  });
  // `encodePlaceholder` omits an empty props/bindings attribute; without this the
  // stale one would stay on the element and the "empty" state would never save.
  const gone = OPTIONAL_ATTRS.filter((name) => attrs[name] === undefined);
  if (gone.length) host.removeAttributes(gone);
  host.addAttributes(attrs);
}

/**
 * The binding to write. An existing one is kept (the author may have pointed it
 * at a different instance by hand), otherwise the spec's is used — a generated
 * plugin spec always knows its own instance and content type.
 */
function bindingOf(spec: DcmsComponentSpec, existing: DataBinding | undefined): DataBinding | null {
  if (existing) return { ...existing, query: { ...existing.query } };
  if (!spec.binding?.instanceSlug) return null;
  return {
    propPath: spec.binding.propPath,
    instanceSlug: spec.binding.instanceSlug,
    query: { ...defaultQuery(spec) },
  };
}

/**
 * Wire a component instance up: read once, then write on every trait change.
 * Returns the list of watched property names, which the caller can use to avoid
 * registering when there is nothing to watch.
 */
export function bindPlaceholder(host: PlaceholderHost, spec: DcmsComponentSpec): string[] {
  const watched = [...targeted(spec, 'prop'), ...targeted(spec, 'query')].map((t) => t.name);
  readPlaceholder(host, spec);
  if (watched.length === 0) return watched;
  host.on(watched.map((name) => `change:${name}`).join(' '), () => writePlaceholder(host, spec));
  return watched;
}
