import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Layout primitives.
 *
 * Everything is flow layout — sections stacked, rows and grids inside them —
 * because that is what produces responsive output and what the rest of the
 * catalogue composes from. Absolute positioning is available, but only inside an
 * explicit Free canvas container (see specs/utility.ts), so a page cannot end up
 * accidentally pinned to one viewport width.
 *
 * The classes are the same ones `styles/global.css` styles in the starter, so a
 * dropped block looks right immediately and stays restyleable from the theme.
 */
export const layoutSpecs: DcmsComponentSpec[] = [
  {
    type: 'Section',
    label: 'Section',
    category: 'layout',
    tag: 'section',
    icon: 'section',
    acceptsChildren: true,
    order: 0,
    docs: 'A full-width band of the page. Sections stack vertically.',
    traits: [
      {
        name: 'data-width',
        label: 'Content width',
        kind: 'select',
        default: 'contained',
        options: [
          { value: 'contained', label: 'Contained' },
          { value: 'full', label: 'Full width' },
          { value: 'narrow', label: 'Narrow' },
        ],
      },
    ],
    snippet: `<section class="dcms-section"><div class="dcms-container"></div></section>`,
  },
  {
    type: 'Container',
    label: 'Container',
    category: 'layout',
    tag: 'div',
    icon: 'container',
    acceptsChildren: true,
    order: 1,
    docs: 'Centres its contents and caps their width.',
    traits: [],
  },
  {
    type: 'Row',
    label: 'Columns',
    category: 'layout',
    tag: 'div',
    icon: 'row',
    acceptsChildren: true,
    order: 2,
    docs: 'A responsive row of columns; collapses to a single column on mobile.',
    traits: [
      {
        name: 'data-gap',
        label: 'Gap',
        kind: 'select',
        default: 'md',
        options: [
          { value: 'none', label: 'None' },
          { value: 'sm', label: 'Small' },
          { value: 'md', label: 'Medium' },
          { value: 'lg', label: 'Large' },
        ],
      },
      {
        name: 'data-align',
        label: 'Vertical align',
        kind: 'select',
        default: 'stretch',
        options: [
          { value: 'stretch', label: 'Stretch' },
          { value: 'start', label: 'Top' },
          { value: 'center', label: 'Middle' },
          { value: 'end', label: 'Bottom' },
        ],
      },
    ],
    snippet: `<div class="dcms-row" data-gap="md"><div class="dcms-col"></div><div class="dcms-col"></div></div>`,
  },
  {
    type: 'Column',
    label: 'Column',
    category: 'layout',
    tag: 'div',
    icon: 'container',
    acceptsChildren: true,
    order: 3,
    docs: 'One cell of a Columns row.',
    traits: [
      {
        name: 'data-span',
        label: 'Width',
        kind: 'select',
        default: 'auto',
        options: [
          { value: 'auto', label: 'Equal' },
          { value: '1-4', label: 'One quarter' },
          { value: '1-3', label: 'One third' },
          { value: '1-2', label: 'Half' },
          { value: '2-3', label: 'Two thirds' },
          { value: '3-4', label: 'Three quarters' },
        ],
      },
    ],
  },
  {
    type: 'Grid',
    label: 'Grid',
    category: 'layout',
    tag: 'div',
    icon: 'grid',
    acceptsChildren: true,
    order: 4,
    docs: 'An auto-fitting grid of equal cells.',
    traits: [
      { name: 'data-columns', label: 'Columns', kind: 'number', default: 3 },
      {
        name: 'data-gap',
        label: 'Gap',
        kind: 'select',
        default: 'md',
        options: [
          { value: 'sm', label: 'Small' },
          { value: 'md', label: 'Medium' },
          { value: 'lg', label: 'Large' },
        ],
      },
    ],
    snippet: `<div class="dcms-grid" data-columns="3" data-gap="md"><div class="dcms-cell"></div><div class="dcms-cell"></div><div class="dcms-cell"></div></div>`,
  },
  {
    type: 'Stack',
    label: 'Stack',
    category: 'layout',
    tag: 'div',
    icon: 'stack',
    acceptsChildren: true,
    order: 5,
    docs: 'Stacks its children with an even gap.',
    traits: [
      {
        name: 'data-direction',
        label: 'Direction',
        kind: 'select',
        default: 'column',
        options: [
          { value: 'column', label: 'Vertical' },
          { value: 'row', label: 'Horizontal' },
        ],
      },
      {
        name: 'data-gap',
        label: 'Gap',
        kind: 'select',
        default: 'md',
        options: [
          { value: 'sm', label: 'Small' },
          { value: 'md', label: 'Medium' },
          { value: 'lg', label: 'Large' },
        ],
      },
    ],
  },
  {
    type: 'Spacer',
    label: 'Spacer',
    category: 'layout',
    tag: 'div',
    icon: 'spacer',
    acceptsChildren: false,
    order: 6,
    docs: 'Vertical breathing room between blocks.',
    traits: [
      {
        name: 'data-size',
        label: 'Height',
        kind: 'select',
        default: 'md',
        options: [
          { value: 'sm', label: 'Small' },
          { value: 'md', label: 'Medium' },
          { value: 'lg', label: 'Large' },
          { value: 'xl', label: 'Extra large' },
        ],
      },
    ],
    snippet: `<div class="dcms-spacer" data-size="md"></div>`,
  },
  {
    type: 'Divider',
    label: 'Divider',
    category: 'layout',
    tag: 'hr',
    icon: 'divider',
    acceptsChildren: false,
    order: 7,
    docs: 'A horizontal rule.',
    traits: [],
    snippet: `<hr class="dcms-divider" />`,
  },
];
