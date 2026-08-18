import {
  BINDINGS_ATTR,
  COMPONENT_ATTR,
  PROPS_ATTR,
  identityClassOf,
  type DcmsComponentSpec,
  type TraitSpec,
} from '@dcms/gjs-schema';
import type * as Monaco from 'monaco-editor';

/**
 * Teaches Monaco's HTML worker about the DCMS component catalogue.
 *
 * Monaco's html language service accepts "custom data" describing tags and
 * attributes, and it runs inside the stock html web worker — so tag completion,
 * attribute completion, attribute-value completion and hover documentation for
 * every block and every tenant-plugin component come for free, off the main
 * thread, with no provider of our own.
 *
 * The data is generated from the same `DcmsComponentSpec` list the palette and
 * the canvas use, which is what stops the code view from offering an attribute
 * the block ignores, or omitting one it needs.
 */

/** Monaco's HTMLDataV1 shape, declared locally to avoid a deep import. */
interface HtmlDataValue {
  name: string;
  description?: string;
}
interface HtmlDataAttribute {
  name: string;
  description?: string;
  values?: HtmlDataValue[];
  valueSet?: string;
}
interface HtmlDataTag {
  name: string;
  description?: string;
  attributes: HtmlDataAttribute[];
}
export interface HtmlDataV1 {
  version: 1.1;
  tags?: HtmlDataTag[];
  globalAttributes?: HtmlDataAttribute[];
}

export function buildHtmlData(specs: readonly DcmsComponentSpec[]): HtmlDataV1 {
  return {
    version: 1.1,
    // Attributes are declared globally rather than per tag: the catalogue reuses
    // the same tags (a dozen components are a `<section>`), so per-tag entries
    // would collide and the last one registered would win.
    globalAttributes: [
      {
        name: COMPONENT_ATTR,
        description: pluginAttrDoc(specs),
        values: specs
          .filter((s) => s.category === 'plugin')
          .map((s) => ({ name: s.type, description: s.docs })),
      },
      {
        name: PROPS_ATTR,
        description:
          'JSON object of props for a plugin component, e.g. `{"heading":"Latest posts"}`. Read by the published hydration runtime.',
      },
      {
        name: BINDINGS_ATTR,
        description:
          'JSON array of data bindings, e.g. `[{"propPath":"items","instanceSlug":"blog","query":{"contentType":"post"}}]`.',
      },
      ...traitAttributes(specs),
    ],
  };
}

/** One completion entry per distinct trait attribute across the catalogue. */
function traitAttributes(specs: readonly DcmsComponentSpec[]): HtmlDataAttribute[] {
  const byName = new Map<string, { trait: TraitSpec; owners: string[] }>();

  for (const spec of specs) {
    for (const trait of spec.traits) {
      // Prop-targeted traits live inside data-dcms-props, not as attributes.
      if (trait.target === 'prop') continue;
      // Plain HTML attributes (href, src, alt) are already known to Monaco.
      if (!trait.name.startsWith('data-')) continue;

      const existing = byName.get(trait.name);
      if (existing) {
        existing.owners.push(spec.label);
        // Merge option sets so a shared attribute offers every valid value.
        if (trait.options && existing.trait.options) {
          const seen = new Set(existing.trait.options.map((o) => o.value));
          existing.trait = {
            ...existing.trait,
            options: [...existing.trait.options, ...trait.options.filter((o) => !seen.has(o.value))],
          };
        }
      } else {
        byName.set(trait.name, { trait: { ...trait }, owners: [spec.label] });
      }
    }
  }

  return [...byName.values()].map(({ trait, owners }) => ({
    name: trait.name,
    description: [trait.label, trait.description, `Used by: ${owners.slice(0, 6).join(', ')}`]
      .filter(Boolean)
      .join('\n\n'),
    values: trait.options?.map((o) => ({ name: o.value, description: o.label })),
    valueSet: trait.kind === 'checkbox' ? 'v' : undefined,
  }));
}

function pluginAttrDoc(specs: readonly DcmsComponentSpec[]): string {
  const plugins = specs.filter((s) => s.category === 'plugin');
  return [
    'Marks a data-bound plugin component. The published page renders it as an empty placeholder and the hydration runtime fills it in from the tenant content API.',
    plugins.length ? `Available: ${plugins.map((s) => s.type).join(', ')}.` : '',
  ]
    .filter(Boolean)
    .join('\n\n');
}

/**
 * Class-name completions for `class="…"`.
 *
 * Monaco's html data cannot express "these classes exist", so the identity
 * classes are offered through a completion provider instead — see
 * `registerBuilderIntellisense` in intellisense.ts. This export is what feeds it,
 * alongside the classes the project's own CSS defines.
 */
export function identityClasses(specs: readonly DcmsComponentSpec[]): { name: string; doc: string }[] {
  return specs
    .filter((s) => s.category !== 'plugin')
    .map((s) => ({
      name: identityClassOf(s),
      doc: `${s.label}${s.docs ? ` — ${s.docs}` : ''}`,
    }));
}

let registered: Monaco.IDisposable | null = null;

/**
 * Install (or replace) the DCMS html data. Called again whenever the tenant's
 * enabled plugins change, since that changes which components exist.
 */
export function registerHtmlData(monaco: typeof Monaco, specs: readonly DcmsComponentSpec[]): void {
  registered?.dispose();
  const data = buildHtmlData(specs);
  monaco.languages.html.htmlDefaults.setOptions({
    ...monaco.languages.html.htmlDefaults.options,
    data: {
      useDefaultDataProvider: true,
      dataProviders: { dcms: data as never },
    },
  });
  // `setOptions` has no removal API, so replacing means overwriting; the
  // disposable exists so a future provider-based path can clean up properly.
  registered = { dispose: () => undefined };
}
