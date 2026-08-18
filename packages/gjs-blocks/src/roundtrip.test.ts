import { parseHtml } from '@dcms/gjs-parse';
import { COMPONENT_ATTR, classesOf, identityClassOf, type DcmsComponentSpec } from '@dcms/gjs-schema';
import { describe, expect, it } from 'vitest';
import { matchesSpec, viewOfElement, viewOfParsedNode } from './match';
import { defaultSnippet } from './register';
import { BUILTIN_SPECS } from './specs';

/**
 * The load-bearing test for the whole format.
 *
 * Mode A stores HTML, so every block must survive being written to a file and
 * read back: same component type, same markup. A block that fails this does not
 * merely look wrong — it silently degrades a Hero into an anonymous `<section>`
 * on the next save, and the author's structure is gone.
 *
 * The parse here is the real worker parser (`@dcms/gjs-parse`), and the matcher
 * is the real one GrapesJS is handed, so this covers the actual production path
 * rather than a stand-in for it.
 */

/** Locate the node in a parsed snippet that the spec should claim. */
function findMatch(spec: DcmsComponentSpec, html: string) {
  const { nodes } = parseHtml(html);
  const stack = [...nodes];
  while (stack.length) {
    const node = stack.shift()!;
    if (matchesSpec(viewOfParsedNode(node), spec)) return node;
    if (node.childNodes) stack.push(...node.childNodes);
  }
  return null;
}

describe('block catalogue', () => {
  it('registers a decent number of blocks', () => {
    expect(BUILTIN_SPECS.length).toBeGreaterThanOrEqual(60);
  });

  it('has unique types', () => {
    const types = BUILTIN_SPECS.map((s) => s.type);
    expect(new Set(types).size).toBe(types.length);
  });

  it('has unique identity classes', () => {
    const classes = BUILTIN_SPECS.filter((s) => s.category !== 'plugin').map(identityClassOf);
    expect(new Set(classes).size).toBe(classes.length);
  });

  it('gives every block a label, docs and traits array', () => {
    for (const spec of BUILTIN_SPECS) {
      expect(spec.label, spec.type).toBeTruthy();
      expect(spec.docs, spec.type).toBeTruthy();
      expect(Array.isArray(spec.traits), spec.type).toBe(true);
    }
  });

  it('gives every trait a stable name and label', () => {
    for (const spec of BUILTIN_SPECS) {
      const names = spec.traits.map((t) => t.name);
      expect(new Set(names).size, `${spec.type} has duplicate trait names`).toBe(names.length);
      for (const trait of spec.traits) {
        expect(trait.label, `${spec.type}.${trait.name}`).toBeTruthy();
      }
    }
  });

  it('gives every select trait options, and every option-bearing trait a valid default', () => {
    for (const spec of BUILTIN_SPECS) {
      for (const trait of spec.traits) {
        if (trait.kind !== 'select') continue;
        expect(trait.options?.length, `${spec.type}.${trait.name}`).toBeGreaterThan(0);
        if (trait.default !== undefined) {
          expect(
            trait.options!.some((o) => o.value === trait.default),
            `${spec.type}.${trait.name} default "${trait.default}" is not one of its options`,
          ).toBe(true);
        }
      }
    }
  });
});

describe('snippet round trip', () => {
  it.each(BUILTIN_SPECS.map((s) => [s.type, s] as const))(
    '%s survives parse and is re-identified',
    (_type, spec) => {
      const html = spec.snippet ?? defaultSnippet(spec);
      const node = findMatch(spec, html);
      expect(node, `${spec.type}: its own snippet is not matched by its own matcher`).not.toBeNull();
    },
  );

  it.each(BUILTIN_SPECS.map((s) => [s.type, s] as const))(
    '%s carries its identity class in its snippet',
    (_type, spec) => {
      if (spec.category === 'plugin') return;
      const html = spec.snippet ?? defaultSnippet(spec);
      expect(html).toContain(identityClassOf(spec));
    },
  );

  it.each(BUILTIN_SPECS.map((s) => [s.type, s] as const))(
    '%s parses to the tag its spec declares',
    (_type, spec) => {
      const html = spec.snippet ?? defaultSnippet(spec);
      const node = findMatch(spec, html);
      expect(node?.tagName).toBe(spec.tag.toLowerCase());
    },
  );

  it('is stable across a second parse of the same markup', () => {
    // Parsing is deterministic, so a page loaded, saved and loaded again must
    // produce the identical tree — this is what makes autosave non-destructive.
    for (const spec of BUILTIN_SPECS) {
      const html = spec.snippet ?? defaultSnippet(spec);
      expect(parseHtml(html).nodes).toEqual(parseHtml(html).nodes);
    }
  });
});

describe('matchesSpec', () => {
  const hero = BUILTIN_SPECS.find((s) => s.type === 'Hero')!;

  it('matches on tag plus identity class', () => {
    expect(matchesSpec(viewOfParsedNode({ tagName: 'section', attributes: { class: 'dcms-hero' } }), hero)).toBe(true);
  });

  it('rejects the right class on the wrong tag', () => {
    expect(matchesSpec(viewOfParsedNode({ tagName: 'div', attributes: { class: 'dcms-hero' } }), hero)).toBe(false);
  });

  it('rejects the right tag without the class', () => {
    expect(matchesSpec(viewOfParsedNode({ tagName: 'section', attributes: { class: 'other' } }), hero)).toBe(false);
  });

  it('tolerates extra classes in any order', () => {
    expect(
      matchesSpec(viewOfParsedNode({ tagName: 'section', attributes: { class: 'a dcms-hero b' } }), hero),
    ).toBe(true);
  });

  it('identifies plugin components by their published placeholder attribute', () => {
    const spec: DcmsComponentSpec = {
      type: 'BlogList',
      label: 'Blog',
      category: 'plugin',
      tag: 'div',
      acceptsChildren: false,
      traits: [],
    };
    expect(matchesSpec(viewOfParsedNode({ tagName: 'div', attributes: { [COMPONENT_ATTR]: 'BlogList' } }), spec)).toBe(true);
    expect(matchesSpec(viewOfParsedNode({ tagName: 'div', attributes: { [COMPONENT_ATTR]: 'Other' } }), spec)).toBe(false);
  });

  it('reads a DOM-shaped element the same way as a parsed node', () => {
    // GrapesJS uppercases tagName on both real elements and its synthetic ones.
    const el = {
      tagName: 'SECTION',
      getAttribute: (name: string) => (name === 'class' ? 'dcms-hero' : null),
    };
    expect(matchesSpec(viewOfElement(el), hero)).toBe(true);
  });
});

describe('defaultSnippet', () => {
  it('emits the published placeholder contract for a plugin component', () => {
    const spec: DcmsComponentSpec = {
      type: 'BlogList',
      label: 'Blog',
      category: 'plugin',
      tag: 'div',
      acceptsChildren: false,
      binding: { propPath: 'items', contentType: 'post', instanceSlug: 'blog' },
      traits: [{ name: 'heading', label: 'Heading', kind: 'text', target: 'prop', default: 'Posts' }],
    };
    const html = defaultSnippet(spec);

    const node = findMatch(spec, html)!;
    expect(node.attributes?.[COMPONENT_ATTR]).toBe('BlogList');
    expect(JSON.parse(node.attributes!['data-dcms-props'])).toEqual({ heading: 'Posts' });
    expect(JSON.parse(node.attributes!['data-dcms-bindings'])).toEqual([
      { propPath: 'items', instanceSlug: 'blog', query: { contentType: 'post' } },
    ]);
  });

  it('omits bindings for a plugin component with no instance chosen yet', () => {
    const spec: DcmsComponentSpec = {
      type: 'SearchBox',
      label: 'Search',
      category: 'plugin',
      tag: 'div',
      acceptsChildren: false,
      traits: [],
    };
    expect(defaultSnippet(spec)).not.toContain('data-dcms-bindings');
  });

  it('builds a container or a void element from the spec', () => {
    const container: DcmsComponentSpec = {
      type: 'Box',
      label: 'Box',
      category: 'layout',
      tag: 'div',
      acceptsChildren: true,
      traits: [],
    };
    expect(defaultSnippet(container)).toBe('<div class="dcms-box"></div>');

    const void_: DcmsComponentSpec = { ...container, type: 'Rule', tag: 'hr', acceptsChildren: false };
    expect(defaultSnippet(void_)).toBe('<hr class="dcms-rule" />');
  });

  it('includes attribute-targeted trait defaults but not prop-targeted ones', () => {
    const spec: DcmsComponentSpec = {
      type: 'Box',
      label: 'Box',
      category: 'layout',
      tag: 'div',
      acceptsChildren: true,
      traits: [
        { name: 'data-size', label: 'Size', kind: 'text', default: 'md' },
        { name: 'heading', label: 'Heading', kind: 'text', target: 'prop', default: 'x' },
      ],
    };
    expect(defaultSnippet(spec)).toBe('<div class="dcms-box" data-size="md"></div>');
  });
});

describe('classesOf', () => {
  it('puts the identity class first so it reads as the component name', () => {
    const hero = BUILTIN_SPECS.find((s) => s.type === 'Hero')!;
    expect(classesOf(hero)[0]).toBe('dcms-hero');
  });
});
