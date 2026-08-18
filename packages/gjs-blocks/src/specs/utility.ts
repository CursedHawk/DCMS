import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Utility components — the escape hatches.
 *
 * `FreeCanvas` is the one place absolute positioning is allowed. Making it an
 * explicit container (rather than a page-wide mode, as the old Mode A editor
 * had) means a page cannot end up accidentally pinned to one viewport width:
 * everything outside the container still reflows, and the container itself is
 * one clearly-marked area an author opted into.
 *
 * `CustomCode` and `Embed` are deliberately not editable in the canvas — their
 * content is whatever the author wrote, and letting the visual editor rewrite it
 * would be the fastest way to break a third-party snippet.
 */
export const utilitySpecs: DcmsComponentSpec[] = [
  {
    type: 'FreeCanvas',
    label: 'Free canvas',
    category: 'utility',
    tag: 'div',
    icon: 'canvas',
    acceptsChildren: true,
    order: 0,
    docs: 'Position children freely inside this box. Everything outside it still reflows.',
    traits: [
      { name: 'data-width', label: 'Design width (px)', kind: 'number', default: 1200 },
      { name: 'data-height', label: 'Minimum height (px)', kind: 'number', default: 600 },
    ],
    snippet: `<div class="dcms-free-canvas" data-width="1200" data-height="600"></div>`,
  },
  {
    type: 'CustomCode',
    label: 'Custom code',
    category: 'utility',
    tag: 'div',
    icon: 'raw',
    acceptsChildren: false,
    order: 1,
    docs: 'Your own HTML, CSS and JavaScript. This is the deliberate way to add a script.',
    traits: [{ name: 'data-label', label: 'Label', kind: 'text', default: 'Custom code' }],
    snippet: `<div class="dcms-custom-code" data-label="Custom code"></div>`,
  },
  {
    type: 'Embed',
    label: 'Embed',
    category: 'utility',
    tag: 'div',
    icon: 'embed',
    acceptsChildren: false,
    order: 2,
    docs: 'An iframe from another site — a form, a booking widget, a player.',
    traits: [
      { name: 'data-src', label: 'URL', kind: 'url', required: true },
      { name: 'data-title', label: 'Title', kind: 'text', description: 'Read out by screen readers.' },
      { name: 'data-ratio', label: 'Aspect ratio', kind: 'text', default: '16 / 9' },
    ],
    snippet: `<div class="dcms-embed" data-src="" data-ratio="16 / 9"></div>`,
  },
  {
    type: 'RawHtml',
    label: 'Raw HTML',
    category: 'utility',
    tag: 'div',
    icon: 'raw',
    acceptsChildren: false,
    order: 3,
    docs: 'Markup passed straight through, untouched by the editor.',
    traits: [],
    snippet: `<div class="dcms-raw-html"></div>`,
  },
  {
    type: 'Conditional',
    label: 'Conditional',
    category: 'utility',
    tag: 'div',
    icon: 'raw',
    acceptsChildren: true,
    order: 4,
    docs: 'Shows its contents only when a condition holds — for example a signed-in visitor.',
    traits: [
      {
        name: 'data-when',
        label: 'Show when',
        kind: 'select',
        default: 'always',
        options: [
          { value: 'always', label: 'Always' },
          { value: 'signed-in', label: 'Visitor is signed in' },
          { value: 'signed-out', label: 'Visitor is signed out' },
        ],
      },
    ],
    snippet: `<div class="dcms-conditional" data-when="signed-in"></div>`,
  },
];
