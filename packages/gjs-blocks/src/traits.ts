import type { TraitSpec } from '@dcms/gjs-schema';

/**
 * `TraitSpec` → GrapesJS trait definition.
 *
 * GrapesJS ships text/number/checkbox/select/color traits and lets a plugin
 * register more. The kinds that have no built-in equivalent (`media`,
 * `contentRef`, `tags`, `code`) are declared here with a `dcms-*` type and are
 * rendered by the admin's own Traits panel, which runs in `custom: true` mode —
 * so a trait can open the real media library instead of asking for a URL.
 */

export interface GrapesTrait {
  type: string;
  name: string;
  label: string;
  /** Written into `data-dcms-props` rather than as an attribute. */
  changeProp?: boolean;
  options?: { id: string; label: string }[];
  placeholder?: string;
  min?: number;
  /** Passed through to the custom trait renderer. */
  dcms?: { kind: TraitSpec['kind']; accepts?: TraitSpec['accepts']; description?: string };
}

const NATIVE: Partial<Record<TraitSpec['kind'], string>> = {
  text: 'text',
  longText: 'textarea',
  number: 'number',
  checkbox: 'checkbox',
  select: 'select',
  color: 'color',
  url: 'text',
  date: 'text',
};

export function toGrapesTrait(trait: TraitSpec): GrapesTrait {
  const native = NATIVE[trait.kind];
  const result: GrapesTrait = {
    type: native ?? `dcms-${trait.kind}`,
    name: trait.name,
    label: trait.label,
    dcms: { kind: trait.kind, accepts: trait.accepts, description: trait.description },
  };
  // A prop- or query-targeted trait feeds `data-dcms-props` / the binding query,
  // which the component's own model serializes; GrapesJS must not also write it
  // as a bare attribute.
  if (trait.target === 'prop' || trait.target === 'query') result.changeProp = true;
  if (trait.kind === 'select' && trait.options) {
    result.options = trait.options.map((o) => ({ id: o.value, label: o.label }));
  }
  if (trait.kind === 'number') result.min = 0;
  if (trait.kind === 'url') result.placeholder = 'https://…';
  return result;
}

export function toGrapesTraits(traits: readonly TraitSpec[]): GrapesTrait[] {
  return traits.map(toGrapesTrait);
}

/** Trait type ids the admin's Traits panel must know how to render. */
export const CUSTOM_TRAIT_TYPES = ['dcms-media', 'dcms-contentRef', 'dcms-tags', 'dcms-code'] as const;
