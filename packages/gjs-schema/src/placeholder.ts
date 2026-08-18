/**
 * The data-bound / plugin component contract.
 *
 * A plugin component is published as an inert placeholder element carrying its
 * type, its props and its bindings as attributes. The already-deployed client
 * runtime (src/Services/Dcms.SiteBuilder/Runtime/hydrate.js) walks
 * `[data-dcms-component]`, fetches each binding from the tenant delivery API and
 * renders the result in place — so emitting exactly this shape from the builder
 * means the publish pipeline needs no plugin-specific code at all.
 *
 *   <div data-dcms-component="BlogList"
 *        data-dcms-props='{"heading":"Latest posts"}'
 *        data-dcms-bindings='[{"propPath":"items","instanceSlug":"blog","query":{"contentType":"post","pageSize":10}}]'></div>
 *
 * This module is the single encoder/decoder: the block library, the Monaco
 * intellisense worker and the site assembler all go through it.
 */

export const COMPONENT_ATTR = 'data-dcms-component';
export const PROPS_ATTR = 'data-dcms-props';
export const BINDINGS_ATTR = 'data-dcms-bindings';
/** Set by hydrate.js at runtime; the builder must never emit it. */
export const HYDRATED_ATTR = 'data-dcms-hydrated';

/** Attributes the builder owns. Anything else on a placeholder is author markup. */
export const PLACEHOLDER_ATTRS = [COMPONENT_ATTR, PROPS_ATTR, BINDINGS_ATTR] as const;

/** One binding, in the flat wire shape hydrate.js emits and reads. */
export interface DataBinding {
  /** Which prop of the component receives the fetched data, e.g. "items". */
  propPath: string;
  /** Plugin instance slug on the tenant's content API. */
  instanceSlug: string;
  /** Delivery query; `contentType` is required for hydrate.js to build the URL. */
  query: BindingQuery;
}

export interface BindingQuery {
  contentType?: string;
  pageSize?: number;
  page?: number;
  [key: string]: unknown;
}

export interface PlaceholderData {
  /** Component type, e.g. "BlogList". */
  component: string;
  props: Record<string, unknown>;
  bindings: DataBinding[];
}

/** The legacy nested binding shape hydrate.js still tolerates on read. */
interface NestedBinding {
  propPath?: unknown;
  source?: { instanceSlug?: unknown; query?: unknown };
  instanceSlug?: unknown;
  query?: unknown;
}

function normalizeBinding(raw: unknown): DataBinding | null {
  if (!raw || typeof raw !== 'object') return null;
  const b = raw as NestedBinding;
  const instanceSlug = typeof b.instanceSlug === 'string' ? b.instanceSlug
    : typeof b.source?.instanceSlug === 'string' ? b.source.instanceSlug
    : null;
  if (!instanceSlug) return null;
  const rawQuery = b.query ?? b.source?.query;
  const query = rawQuery && typeof rawQuery === 'object' && !Array.isArray(rawQuery)
    ? (rawQuery as BindingQuery)
    : {};
  return {
    propPath: typeof b.propPath === 'string' && b.propPath ? b.propPath : 'items',
    instanceSlug,
    query,
  };
}

function parseJson<T>(value: string | null | undefined, fallback: T): T {
  if (!value) return fallback;
  try {
    return JSON.parse(value) as T;
  } catch {
    return fallback;
  }
}

/**
 * Build the attribute map for a placeholder. Empty props/bindings are omitted
 * rather than written as `{}`/`[]` so a component that has not been bound yet
 * produces a clean, small diff.
 */
export function encodePlaceholder(data: PlaceholderData): Record<string, string> {
  const attrs: Record<string, string> = { [COMPONENT_ATTR]: data.component };
  if (Object.keys(data.props ?? {}).length > 0) {
    attrs[PROPS_ATTR] = JSON.stringify(data.props);
  }
  if ((data.bindings ?? []).length > 0) {
    attrs[BINDINGS_ATTR] = JSON.stringify(
      data.bindings.map((b) => ({
        propPath: b.propPath,
        instanceSlug: b.instanceSlug,
        query: b.query ?? {},
      })),
    );
  }
  return attrs;
}

/**
 * Read a placeholder back out of an attribute map. Returns null when the element
 * is not a placeholder. Malformed props/bindings JSON degrades to empty rather
 * than throwing — the same tolerance hydrate.js has, so a hand-edited file that
 * is momentarily broken still opens in the builder.
 */
export function decodePlaceholder(
  attributes: Record<string, string | null | undefined>,
): PlaceholderData | null {
  const component = attributes[COMPONENT_ATTR];
  if (typeof component !== 'string' || !component) return null;

  const props = parseJson<Record<string, unknown>>(attributes[PROPS_ATTR], {});
  const rawBindings = parseJson<unknown[]>(attributes[BINDINGS_ATTR], []);
  const bindings = (Array.isArray(rawBindings) ? rawBindings : [])
    .map(normalizeBinding)
    .filter((b): b is DataBinding => b !== null);

  return {
    component,
    props: props && typeof props === 'object' && !Array.isArray(props) ? props : {},
    bindings,
  };
}

/** True when the attribute map describes a data-bound / plugin placeholder. */
export function isPlaceholder(attributes: Record<string, string | null | undefined>): boolean {
  return typeof attributes[COMPONENT_ATTR] === 'string' && attributes[COMPONENT_ATTR] !== '';
}

/**
 * Which JSON parses failed, for the code view's diagnostics. Distinguishing this
 * from `decodePlaceholder`'s silent fallback matters: the builder must keep
 * working on a broken file, but the author still needs to be told it is broken.
 */
export function placeholderErrors(
  attributes: Record<string, string | null | undefined>,
): { attribute: string; message: string }[] {
  const errors: { attribute: string; message: string }[] = [];
  for (const attr of [PROPS_ATTR, BINDINGS_ATTR]) {
    const raw = attributes[attr];
    if (!raw) continue;
    try {
      const parsed: unknown = JSON.parse(raw);
      const wantsArray = attr === BINDINGS_ATTR;
      if (wantsArray && !Array.isArray(parsed)) {
        errors.push({ attribute: attr, message: `${attr} must be a JSON array of bindings.` });
      } else if (!wantsArray && (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed))) {
        errors.push({ attribute: attr, message: `${attr} must be a JSON object.` });
      }
    } catch (e) {
      errors.push({ attribute: attr, message: `${attr} is not valid JSON: ${(e as Error).message}` });
    }
  }
  return errors;
}
