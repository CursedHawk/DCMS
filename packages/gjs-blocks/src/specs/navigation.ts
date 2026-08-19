import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Navigation components.
 *
 * These are plain markup, not scripted widgets: the navbar's mobile toggle, the
 * tabs and the accordion all work through `<details>`/`<summary>` and the
 * `:checked` trick, so a published Mode A page needs no JavaScript beyond the
 * plugin hydration runtime. That keeps the output fast and means a block behaves
 * the same in the canvas as it does live.
 */
export const navigationSpecs: DcmsComponentSpec[] = [
  {
    type: 'Navbar',
    label: 'Navbar',
    category: 'navigation',
    tag: 'header',
    icon: 'navbar',
    acceptsChildren: true,
    order: 0,
    docs: 'Site header with a logo and links; collapses behind a toggle on small screens.',
    traits: [
      {
        name: 'data-layout',
        label: 'Layout',
        kind: 'select',
        default: 'between',
        options: [
          { value: 'between', label: 'Logo left, links right' },
          { value: 'center', label: 'Centred' },
          { value: 'stacked', label: 'Stacked' },
        ],
      },
      { name: 'data-sticky', label: 'Stick to top', kind: 'checkbox' },
    ],
    snippet: `<header class="dcms-navbar" data-layout="between">
  <a class="dcms-logo" href="/">Site name</a>
  <input class="dcms-navbar-toggle" id="nav-toggle" type="checkbox" hidden />
  <label class="dcms-navbar-burger" for="nav-toggle" aria-label="Menu"><span></span></label>
  <nav class="dcms-navbar-links" data-dcms-nav="menu">
    <a href="/">Home</a>
    <a href="/about">About</a>
    <a href="/contact">Contact</a>
  </nav>
</header>`,
  },
  {
    type: 'Menu',
    label: 'Menu',
    category: 'navigation',
    tag: 'nav',
    icon: 'menu',
    acceptsChildren: true,
    order: 1,
    docs: 'A list of links.',
    traits: [
      {
        name: 'data-direction',
        label: 'Direction',
        kind: 'select',
        default: 'row',
        options: [
          { value: 'row', label: 'Horizontal' },
          { value: 'column', label: 'Vertical' },
        ],
      },
    ],
    snippet: `<nav class="dcms-menu" data-direction="row" data-dcms-nav="menu"><a href="/">Home</a><a href="/about">About</a></nav>`,
  },
  {
    type: 'Breadcrumbs',
    label: 'Breadcrumbs',
    category: 'navigation',
    tag: 'nav',
    icon: 'breadcrumbs',
    acceptsChildren: true,
    order: 2,
    docs: 'Shows where this page sits in the site. The trail is filled in from the page URL, so one breadcrumb bar in a shared region is correct on every page.',
    traits: [{ name: 'data-home-label', label: 'Home label', kind: 'text', default: 'Home' }],
    snippet: `<nav class="dcms-breadcrumbs" aria-label="Breadcrumb" data-dcms-nav="breadcrumbs" data-home-label="Home"><ol><li><a href="/">Home</a></li><li aria-current="page">This page</li></ol></nav>`,
  },
  {
    type: 'Tabs',
    label: 'Tabs',
    category: 'navigation',
    tag: 'div',
    icon: 'tabs',
    acceptsChildren: true,
    order: 3,
    docs: 'Switchable panels. Works without JavaScript.',
    traits: [],
    snippet: `<div class="dcms-tabs">
  <input type="radio" name="tabs" id="tab-1" checked hidden />
  <label for="tab-1">First</label>
  <div class="dcms-tab-panel"><p>First panel.</p></div>
  <input type="radio" name="tabs" id="tab-2" hidden />
  <label for="tab-2">Second</label>
  <div class="dcms-tab-panel"><p>Second panel.</p></div>
</div>`,
  },
  {
    type: 'Accordion',
    label: 'Accordion',
    category: 'navigation',
    tag: 'div',
    icon: 'accordion',
    acceptsChildren: true,
    order: 4,
    docs: 'Collapsible sections built on native <details>.',
    traits: [],
    snippet: `<div class="dcms-accordion">
  <details open><summary>First question</summary><p>Its answer.</p></details>
  <details><summary>Second question</summary><p>Its answer.</p></details>
</div>`,
  },
  {
    type: 'AnchorLink',
    label: 'Anchor',
    category: 'navigation',
    tag: 'a',
    icon: 'anchor',
    acceptsChildren: true,
    order: 5,
    docs: 'Jumps to an element on this page by its id.',
    traits: [{ name: 'href', label: 'Target id', kind: 'text', default: '#section' }],
    snippet: `<a class="dcms-anchor-link" href="#section">Jump to section</a>`,
  },
  {
    type: 'Pagination',
    label: 'Pagination',
    category: 'navigation',
    tag: 'nav',
    icon: 'pagination',
    acceptsChildren: true,
    order: 6,
    docs: 'Page-through links for a long list.',
    traits: [],
    snippet: `<nav class="dcms-pagination" aria-label="Pagination"><a href="#" rel="prev">Previous</a><a href="#" aria-current="page">1</a><a href="#">2</a><a href="#" rel="next">Next</a></nav>`,
  },
  {
    type: 'Sidebar',
    label: 'Sidebar',
    category: 'navigation',
    tag: 'aside',
    icon: 'sidebar',
    acceptsChildren: true,
    order: 7,
    docs: 'A secondary column beside the main content.',
    traits: [
      {
        name: 'data-side',
        label: 'Side',
        kind: 'select',
        default: 'left',
        options: [
          { value: 'left', label: 'Left' },
          { value: 'right', label: 'Right' },
        ],
      },
    ],
  },
  {
    type: 'Footer',
    label: 'Footer',
    category: 'navigation',
    tag: 'footer',
    icon: 'footer',
    acceptsChildren: true,
    order: 8,
    docs: 'The bottom band of the page.',
    traits: [],
    snippet: `<footer class="dcms-footer">
  <div class="dcms-container">
    <nav class="dcms-menu" data-direction="row" data-dcms-nav="menu"><a href="/">Home</a><a href="/privacy">Privacy</a></nav>
    <p class="dcms-footer-note">© Your organisation</p>
  </div>
</footer>`,
  },
];
