import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Text primitives.
 *
 * These are thin: GrapesJS already makes any element with text content editable
 * in place, so the value a spec adds here is a readable name in the layer tree,
 * a palette tile with sensible starting markup, and traits for the choices that
 * are not just typing (heading level, list style, alignment).
 */
export const typographySpecs: DcmsComponentSpec[] = [
  {
    type: 'Heading',
    label: 'Heading',
    category: 'typography',
    tag: 'h2',
    icon: 'heading',
    acceptsChildren: true,
    order: 0,
    docs: 'A section heading. Levels matter for SEO and screen readers, so pick by meaning, not size.',
    traits: [
      {
        name: 'data-level',
        label: 'Level',
        kind: 'select',
        default: 'h2',
        description: 'Use one h1 per page.',
        options: [
          { value: 'h1', label: 'Heading 1' },
          { value: 'h2', label: 'Heading 2' },
          { value: 'h3', label: 'Heading 3' },
          { value: 'h4', label: 'Heading 4' },
          { value: 'h5', label: 'Heading 5' },
          { value: 'h6', label: 'Heading 6' },
        ],
      },
    ],
    snippet: `<h2 class="dcms-heading">Heading</h2>`,
  },
  {
    type: 'Paragraph',
    label: 'Paragraph',
    category: 'typography',
    tag: 'p',
    icon: 'text',
    acceptsChildren: true,
    order: 1,
    docs: 'A block of body text.',
    traits: [],
    snippet: `<p class="dcms-paragraph">Write something worth reading.</p>`,
  },
  {
    type: 'RichText',
    label: 'Rich text',
    category: 'typography',
    tag: 'div',
    icon: 'text',
    acceptsChildren: true,
    order: 2,
    docs: 'Mixed formatted content — headings, paragraphs, lists and links together.',
    traits: [],
    snippet: `<div class="dcms-rich-text"><h3>A short heading</h3><p>Followed by a paragraph, a <a href="#">link</a> and anything else you need.</p></div>`,
  },
  {
    type: 'Blockquote',
    label: 'Quote',
    category: 'typography',
    tag: 'blockquote',
    icon: 'quote',
    acceptsChildren: true,
    order: 3,
    docs: 'A pulled-out quotation with an optional attribution.',
    traits: [],
    snippet: `<blockquote class="dcms-blockquote"><p>The quote itself.</p><cite>Who said it</cite></blockquote>`,
  },
  {
    type: 'List',
    label: 'List',
    category: 'typography',
    tag: 'ul',
    icon: 'list',
    acceptsChildren: true,
    order: 4,
    docs: 'A bulleted or numbered list.',
    traits: [
      {
        name: 'data-style',
        label: 'Marker',
        kind: 'select',
        default: 'disc',
        options: [
          { value: 'disc', label: 'Bullets' },
          { value: 'decimal', label: 'Numbers' },
          { value: 'none', label: 'None' },
        ],
      },
    ],
    snippet: `<ul class="dcms-list"><li>First item</li><li>Second item</li><li>Third item</li></ul>`,
  },
  {
    type: 'CodeBlock',
    label: 'Code',
    category: 'typography',
    tag: 'pre',
    icon: 'code',
    acceptsChildren: true,
    order: 5,
    docs: 'Preformatted text; whitespace is preserved exactly as typed.',
    traits: [{ name: 'data-language', label: 'Language', kind: 'text', default: 'text' }],
    snippet: `<pre class="dcms-code-block" data-language="text"><code>example()</code></pre>`,
  },
  {
    type: 'Badge',
    label: 'Badge',
    category: 'typography',
    tag: 'span',
    icon: 'badge',
    acceptsChildren: true,
    order: 6,
    docs: 'A small pill for a status or label.',
    traits: [
      {
        name: 'data-tone',
        label: 'Tone',
        kind: 'select',
        default: 'brand',
        options: [
          { value: 'brand', label: 'Brand' },
          { value: 'neutral', label: 'Neutral' },
          { value: 'success', label: 'Success' },
          { value: 'warning', label: 'Warning' },
          { value: 'danger', label: 'Danger' },
        ],
      },
    ],
    snippet: `<span class="dcms-badge" data-tone="brand">New</span>`,
  },
];
