// @vitest-environment jsdom
import { handleRequest, type ParsedNode } from '@dcms/gjs-parse';
import { decodePlaceholder, identityClassOf } from '@dcms/gjs-schema';
import grapesjs, { type Editor } from 'grapesjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { defaultSnippet } from '../register';
import { pageBodyOf } from '../serialize';
import { dcmsPlugins } from './dcmsPlugins';
import { listType, pluginSpecs, type PluginManifestLike } from './specs';

/**
 * The generated plugin components, exercised against a real (headless) editor.
 *
 * The generator's own tests prove the specs are shaped right; this proves the
 * whole chain works: registration, parsing markup back into the right type, the
 * trait ⇄ placeholder binding, and — most importantly — that a page containing a
 * plugin block survives a save/load cycle byte-identically. A block that fails
 * that corrupts the author's page on the next autosave.
 */

const manifest: PluginManifestLike = {
  id: 'blog',
  name: 'Blog',
  contentTypes: [
    {
      name: 'post',
      slugField: 'slug',
      fields: [
        { name: 'title', type: 'Text' },
        { name: 'coverImage', type: 'MediaRef' },
      ],
    },
  ],
};

const instances = [
  { id: 'i1', pluginId: 'blog', slug: 'blog', name: 'Company blog', enabled: true },
];

const SPECS = pluginSpecs([manifest], instances);

const htmlMemo = new Map<string, ParsedNode[]>();

function preparse(input: string): string {
  const res = handleRequest({ kind: 'parse-html', id: 1, key: 'test', input });
  if (res.kind !== 'parse-html') throw new Error('parse failed');
  htmlMemo.set(input, res.result.nodes);
  return input;
}

let editor: Editor;

beforeEach(() => {
  const container = document.createElement('div');
  document.body.appendChild(container);
  editor = grapesjs.init({
    container,
    headless: true,
    storageManager: false,
    panels: { defaults: [] },
    parser: {
      parsersCode: { worker: (input) => htmlMemo.get(input) ?? [] },
      parserCode: 'worker',
      optionsHtml: { allowScripts: false, allowUnsafeAttr: false },
    },
    plugins: [(e) => dcmsPlugins(e, { specs: SPECS })],
  });
});

afterEach(() => {
  editor.destroy();
  htmlMemo.clear();
});

/** The first component of `type` anywhere in the canvas. */
function find(type: string) {
  type Node = NonNullable<ReturnType<Editor['getWrapper']>>;
  let found: Node | null = null;
  const visit = (component: Node) => {
    if (found) return;
    if (String(component.get('type') ?? '') === type) {
      found = component;
      return;
    }
    component.components().forEach(visit as never);
  };
  const wrapper = editor.getWrapper();
  if (wrapper) visit(wrapper);
  return found as Node | null;
}

describe('dcmsPlugins', () => {
  it('registers nothing when the tenant has no plugin instances', () => {
    const container = document.createElement('div');
    document.body.appendChild(container);
    const bare = grapesjs.init({
      container,
      headless: true,
      storageManager: false,
      plugins: [(e) => dcmsPlugins(e)],
    });
    expect(bare.BlockManager.getAll().length).toBe(0);
    bare.destroy();
  });

  it('registers a type and a block for every generated spec', () => {
    for (const spec of SPECS) {
      expect(editor.DomComponents.getType(spec.type), spec.type).toBeTruthy();
      expect(editor.BlockManager.get(`dcms-plugin:${spec.type}`), spec.type).toBeTruthy();
    }
  });

  it('prefixes block ids so a generated block cannot shadow a built-in one', () => {
    // A tenant instance named "hero" must not replace the Hero block.
    for (const spec of SPECS) {
      expect(editor.BlockManager.get(spec.type)).toBeFalsy();
    }
  });

  it('groups every block under the instance name', () => {
    for (const spec of SPECS) {
      const block = editor.BlockManager.get(`dcms-plugin:${spec.type}`)!;
      expect((block.get('category') as { id?: string }).id).toBe('Company blog');
    }
  });
});

describe('a generated block on the canvas', () => {
  const list = SPECS.find((s) => s.type === listType('blog', 'post'))!;

  it('drops markup that already is the published placeholder contract', () => {
    const snippet = defaultSnippet(list);
    const decoded = decodePlaceholder(attributesOf(snippet));
    expect(decoded).toMatchObject({
      component: list.type,
      bindings: [{ propPath: 'items', instanceSlug: 'blog', query: { contentType: 'post' } }],
    });
  });

  it.each(SPECS.map((s) => [s.type, s] as const))('re-identifies %s after parsing', (_type, spec) => {
    editor.setComponents(preparse(defaultSnippet(spec)));
    expect(find(spec.type), `${spec.type} was not re-identified`).toBeTruthy();
  });

  it('does not let the author type into a placeholder', () => {
    editor.setComponents(preparse(defaultSnippet(list)));
    const component = find(list.type)!;
    // Its content comes from the content API at run time; anything typed in the
    // canvas would be silently discarded on publish.
    expect(component.get('editable')).toBe(false);
    expect(component.get('droppable')).toBe(false);
  });

  it('seeds its trait values from the markup', () => {
    const html =
      `<div class="${identityClassOf(list)}" data-dcms-component="${list.type}" ` +
      `data-dcms-props='{"heading":"Latest"}' ` +
      `data-dcms-bindings='[{"propPath":"items","instanceSlug":"blog","query":{"contentType":"post","pageSize":3}}]'></div>`;
    editor.setComponents(preparse(html));

    const component = find(list.type)!;
    expect(component.get('heading')).toBe('Latest');
    expect(component.get('pageSize')).toBe(3);
  });

  it('writes a trait change back into the placeholder attributes', () => {
    editor.setComponents(preparse(defaultSnippet(list)));
    const component = find(list.type)!;
    component.set('heading' as never, 'Latest posts' as never);

    const decoded = decodePlaceholder(component.getAttributes() as Record<string, string>);
    expect(decoded?.props.heading).toBe('Latest posts');
  });

  it('writes a query trait into the binding rather than the props', () => {
    editor.setComponents(preparse(defaultSnippet(list)));
    const component = find(list.type)!;
    component.set('pageSize' as never, 4 as never);

    const decoded = decodePlaceholder(component.getAttributes() as Record<string, string>);
    expect(decoded?.bindings[0]!.query.pageSize).toBe(4);
    expect(decoded?.props.pageSize).toBeUndefined();
  });

  it('round-trips a page of plugin blocks without drift', () => {
    const page = SPECS.map(defaultSnippet).join('\n');

    editor.setComponents(preparse(page));
    const first = pageBodyOf(editor.getHtml({ cleanId: true }));
    editor.setComponents(preparse(first));
    const second = pageBodyOf(editor.getHtml({ cleanId: true }));

    expect(second).toBe(first);
  });

  it('keeps the placeholder attributes through a save/load cycle', () => {
    editor.setComponents(preparse(defaultSnippet(list)));
    const saved = pageBodyOf(editor.getHtml({ cleanId: true }));

    expect(saved).toContain(`data-dcms-component="${list.type}"`);
    editor.setComponents(preparse(saved));
    const component = find(list.type)!;
    const decoded = decodePlaceholder(component.getAttributes() as Record<string, string>);
    expect(decoded?.bindings[0]!.instanceSlug).toBe('blog');
  });
});

/** Pull the attribute map out of a one-element snippet, for contract assertions. */
function attributesOf(html: string): Record<string, string> {
  const attrs: Record<string, string> = {};
  const open = html.slice(0, html.indexOf('>'));
  for (const match of open.matchAll(/([\w-]+)\s*=\s*(?:'([^']*)'|"([^"]*)")/g)) {
    attrs[match[1]!] = decode(match[2] ?? match[3] ?? '');
  }
  return attrs;
}

function decode(value: string): string {
  return value.replace(/&#39;/g, "'").replace(/&quot;/g, '"').replace(/&amp;/g, '&');
}
