import {
  COMPONENT_ATTR,
  PROPS_ATTR,
  classesOf,
  defaultAttributes,
  defaultProps,
  defaultQuery,
  encodePlaceholder,
  identityClassOf,
  type DcmsComponentSpec,
} from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { matchersFor } from './match';
import { bindPlaceholder, type PlaceholderHost } from './placeholderModel';
import { toGrapesTraits } from './traits';
import { iconFor } from './icons';

/**
 * One spec → one GrapesJS component type and one palette block.
 *
 * Everything the builder offers goes through here, so the palette, the layer
 * tree, the inspector and the code view's completions can never disagree about
 * what a component is: they all read the same spec.
 */

export interface RegisterOptions {
  /** Prefix for block ids, so generated plugin blocks cannot collide. */
  idPrefix?: string;
}

export function registerSpec(editor: Editor, spec: DcmsComponentSpec, options: RegisterOptions = {}): void {
  registerType(editor, spec);
  registerBlock(editor, spec, options);
}

export function registerSpecs(
  editor: Editor,
  specs: readonly DcmsComponentSpec[],
  options: RegisterOptions = {},
): void {
  for (const spec of specs) registerSpec(editor, spec, options);
}

function registerType(editor: Editor, spec: DcmsComponentSpec): void {
  const { isComponent, isParsedNode } = matchersFor(spec);

  editor.DomComponents.addType(spec.type, {
    isComponent: isComponent as never,
    model: {
      /**
       * A plugin component's settings live inside `data-dcms-props` and the
       * binding query, not as attributes, so the model has to keep the two in
       * step itself — see `placeholderModel.ts`.
       */
      init(this: unknown) {
        if (spec.category !== 'plugin') return;
        bindPlaceholder(this as PlaceholderHost, spec);
      },
      // `isParsedNode` is read off the constructor, not the prototype, so it is
      // attached after addType below.
      defaults: {
        name: spec.label,
        tagName: spec.tag,
        droppable: spec.acceptsChildren,
        // A section is moved as a whole; only its contents are edited in place.
        draggable: true,
        // Plugin placeholders render from live data, so their markup is not
        // hand-editable — letting a user type into one would be lost on publish.
        editable: spec.category !== 'plugin',
        selectable: true,
        highlightable: true,
        traits: toGrapesTraits(spec.traits),
        attributes: { ...defaultAttributes(spec) },
        ...(spec.category === 'plugin' ? { components: [], textable: false } : {}),
      },
    },
  });

  // GrapesJS reads `isParsedNode` from the model constructor (see ParserHtml),
  // and `addType` only forwards a `model.defaults`-shaped object — so the static
  // has to be attached to the registered constructor afterwards.
  const registered = editor.DomComponents.getType(spec.type);
  if (registered) {
    (registered.model as unknown as Record<string, unknown>).isParsedNode = isParsedNode;
  }
}

function registerBlock(editor: Editor, spec: DcmsComponentSpec, { idPrefix = '' }: RegisterOptions): void {
  editor.BlockManager.add(`${idPrefix}${spec.type}`, {
    label: spec.label,
    category: { id: spec.group ?? spec.category, label: spec.group ?? categoryLabel(spec.category) },
    media: iconFor(spec),
    attributes: { title: spec.docs ?? spec.label },
    content: spec.snippet ?? defaultSnippet(spec),
  });
}

const CATEGORY_LABELS: Record<DcmsComponentSpec['category'], string> = {
  layout: 'Layout',
  typography: 'Text',
  media: 'Media',
  navigation: 'Navigation',
  section: 'Sections',
  interactive: 'Interactive',
  form: 'Forms',
  utility: 'Utility',
  plugin: 'Plugins',
};

function categoryLabel(category: DcmsComponentSpec['category']): string {
  return CATEGORY_LABELS[category];
}

/**
 * The markup a block drops when the spec did not supply its own.
 *
 * A plugin component always renders as the published placeholder contract, so
 * what the canvas holds is byte-for-byte what gets committed and what
 * hydrate.js later fills in — there is no editor-only representation to
 * translate at publish time.
 */
export function defaultSnippet(spec: DcmsComponentSpec): string {
  if (spec.category === 'plugin') {
    const attrs = encodePlaceholder({
      component: spec.type,
      props: defaultProps(spec),
      bindings: spec.binding?.instanceSlug
        ? [
            {
              propPath: spec.binding.propPath,
              instanceSlug: spec.binding.instanceSlug,
              query: defaultQuery(spec),
            },
          ]
        : [],
    });
    const rendered = Object.entries(attrs)
      .map(([name, value]) => `${name}='${escapeSingleQuoted(value)}'`)
      .join(' ');
    return `<div class="${identityClassOf(spec)}" ${rendered}></div>`;
  }

  const classes = classesOf(spec).join(' ');
  const attrs = Object.entries(defaultAttributes(spec))
    .map(([name, value]) => ` ${name}="${escapeAttr(value)}"`)
    .join('');
  return spec.acceptsChildren
    ? `<${spec.tag} class="${classes}"${attrs}></${spec.tag}>`
    : `<${spec.tag} class="${classes}"${attrs} />`;
}

/**
 * `data-dcms-props` holds JSON, which contains double quotes, so the attribute
 * is written single-quoted — meaning single quotes inside must be escaped.
 */
function escapeSingleQuoted(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/'/g, '&#39;');
}

function escapeAttr(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/"/g, '&quot;');
}

export { COMPONENT_ATTR, PROPS_ATTR };
