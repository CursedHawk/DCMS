import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Interactive components.
 *
 * The rule here is the same as for navigation: nothing needs a script. Modal,
 * lightbox and carousel are built on `<dialog>`, `:target` and scroll-snap
 * respectively, so what the canvas shows is what the published page does, and a
 * Mode A site stays a static site.
 */
export const interactiveSpecs: DcmsComponentSpec[] = [
  {
    type: 'Button',
    label: 'Button',
    category: 'interactive',
    tag: 'a',
    icon: 'button',
    acceptsChildren: true,
    order: 0,
    docs: 'A link styled as a button.',
    traits: [
      { name: 'href', label: 'Link', kind: 'url', default: '#' },
      {
        name: 'data-variant',
        label: 'Style',
        kind: 'select',
        default: 'solid',
        options: [
          { value: 'solid', label: 'Solid' },
          { value: 'outline', label: 'Outline' },
          { value: 'ghost', label: 'Ghost' },
        ],
      },
      {
        name: 'data-size',
        label: 'Size',
        kind: 'select',
        default: 'md',
        options: [
          { value: 'sm', label: 'Small' },
          { value: 'md', label: 'Medium' },
          { value: 'lg', label: 'Large' },
        ],
      },
      {
        name: 'target',
        label: 'Open in',
        kind: 'select',
        default: '',
        options: [
          { value: '', label: 'Same tab' },
          { value: '_blank', label: 'New tab' },
        ],
      },
    ],
    snippet: `<a class="dcms-button" href="#" data-variant="solid" data-size="md">Click me</a>`,
  },
  {
    type: 'ButtonGroup',
    label: 'Button group',
    category: 'interactive',
    tag: 'div',
    icon: 'button',
    acceptsChildren: true,
    order: 1,
    docs: 'Two or more buttons kept together.',
    traits: [],
    snippet: `<div class="dcms-button-group"><a class="dcms-button" href="#" data-variant="solid">Primary</a><a class="dcms-button" href="#" data-variant="outline">Secondary</a></div>`,
  },
  {
    type: 'Link',
    label: 'Link',
    category: 'interactive',
    tag: 'a',
    icon: 'link',
    acceptsChildren: true,
    order: 2,
    docs: 'A plain text link.',
    traits: [
      { name: 'href', label: 'Link', kind: 'url', default: '#' },
      {
        name: 'target',
        label: 'Open in',
        kind: 'select',
        default: '',
        options: [
          { value: '', label: 'Same tab' },
          { value: '_blank', label: 'New tab' },
        ],
      },
    ],
    snippet: `<a class="dcms-link" href="#">Link text</a>`,
  },
  {
    type: 'Carousel',
    label: 'Carousel',
    category: 'interactive',
    tag: 'div',
    icon: 'carousel',
    acceptsChildren: true,
    order: 3,
    docs: 'A swipeable row of slides, using CSS scroll snapping.',
    traits: [
      {
        name: 'data-per-view',
        label: 'Slides in view',
        kind: 'number',
        default: 1,
      },
    ],
    snippet: `<div class="dcms-carousel" data-per-view="1">
  <div class="dcms-slide"><img class="dcms-image" src="" alt="" loading="lazy" /></div>
  <div class="dcms-slide"><img class="dcms-image" src="" alt="" loading="lazy" /></div>
</div>`,
  },
  {
    type: 'Modal',
    label: 'Modal',
    category: 'interactive',
    tag: 'div',
    icon: 'modal',
    acceptsChildren: true,
    order: 4,
    docs: 'A dialog opened by a link, using the native <dialog> element.',
    traits: [{ name: 'data-modal-id', label: 'Modal id', kind: 'text', default: 'modal-1' }],
    snippet: `<div class="dcms-modal" data-modal-id="modal-1">
  <a class="dcms-button" href="#modal-1">Open</a>
  <dialog id="modal-1" class="dcms-modal-dialog">
    <form method="dialog"><h3>Modal title</h3><p>Its content.</p><button>Close</button></form>
  </dialog>
</div>`,
  },
  {
    type: 'Lightbox',
    label: 'Lightbox',
    category: 'interactive',
    tag: 'div',
    icon: 'lightbox',
    acceptsChildren: true,
    order: 5,
    docs: 'A thumbnail that opens full size, using :target.',
    traits: [],
    snippet: `<div class="dcms-lightbox">
  <a href="#shot-1"><img class="dcms-image" src="" alt="" loading="lazy" /></a>
  <div class="dcms-lightbox-full" id="shot-1"><a href="#"><img src="" alt="" /></a></div>
</div>`,
  },
  {
    type: 'Tooltip',
    label: 'Tooltip',
    category: 'interactive',
    tag: 'span',
    icon: 'tooltip',
    acceptsChildren: true,
    order: 6,
    docs: 'A hint shown on hover or focus.',
    traits: [{ name: 'data-tip', label: 'Tooltip text', kind: 'text', default: 'More detail' }],
    snippet: `<span class="dcms-tooltip" data-tip="More detail" tabindex="0">hover me</span>`,
  },
  {
    type: 'Countdown',
    label: 'Countdown',
    category: 'interactive',
    tag: 'div',
    icon: 'countdown',
    acceptsChildren: false,
    order: 7,
    docs: 'Counts down to a date and time.',
    traits: [
      { name: 'data-deadline', label: 'Deadline', kind: 'date', required: true },
      { name: 'data-expired-text', label: 'When finished', kind: 'text', default: 'It is time.' },
    ],
    snippet: `<div class="dcms-countdown" data-deadline="" data-expired-text="It is time."></div>`,
  },
  {
    type: 'Progress',
    label: 'Progress',
    category: 'interactive',
    tag: 'div',
    icon: 'progress',
    acceptsChildren: false,
    order: 8,
    docs: 'A progress bar.',
    traits: [
      { name: 'data-value', label: 'Value', kind: 'number', default: 50 },
      { name: 'data-max', label: 'Maximum', kind: 'number', default: 100 },
      { name: 'data-label', label: 'Label', kind: 'text' },
    ],
    snippet: `<div class="dcms-progress" data-value="50" data-max="100"><progress value="50" max="100"></progress></div>`,
  },
  {
    type: 'Rating',
    label: 'Rating',
    category: 'interactive',
    tag: 'div',
    icon: 'rating',
    acceptsChildren: false,
    order: 9,
    docs: 'A star rating, for display.',
    traits: [
      { name: 'data-value', label: 'Stars', kind: 'number', default: 5 },
      { name: 'data-out-of', label: 'Out of', kind: 'number', default: 5 },
    ],
    snippet: `<div class="dcms-rating" data-value="5" data-out-of="5" role="img" aria-label="5 out of 5"></div>`,
  },
  {
    type: 'SocialLinks',
    label: 'Social links',
    category: 'interactive',
    tag: 'nav',
    icon: 'social',
    acceptsChildren: true,
    order: 10,
    docs: 'Links to your profiles elsewhere.',
    traits: [],
    snippet: `<nav class="dcms-social-links" aria-label="Social"><a href="#" rel="noopener">Facebook</a><a href="#" rel="noopener">Instagram</a><a href="#" rel="noopener">LinkedIn</a></nav>`,
  },
  {
    type: 'ShareButtons',
    label: 'Share',
    category: 'interactive',
    tag: 'nav',
    icon: 'social',
    acceptsChildren: true,
    order: 11,
    docs: 'Share this page to social networks.',
    traits: [],
    snippet: `<nav class="dcms-share-buttons" aria-label="Share"><a href="#" rel="noopener">Share on Facebook</a><a href="#" rel="noopener">Share on LinkedIn</a></nav>`,
  },
  {
    type: 'CookieBanner',
    label: 'Cookie banner',
    category: 'interactive',
    tag: 'div',
    icon: 'cookie',
    acceptsChildren: true,
    order: 12,
    docs: 'A consent notice pinned to the bottom of the page.',
    traits: [],
    snippet: `<div class="dcms-cookie-banner" role="region" aria-label="Cookies">
  <p>We use cookies to understand how this site is used.</p>
  <a class="dcms-button" href="#" data-variant="solid">Accept</a>
  <a class="dcms-link" href="/privacy">Read more</a>
</div>`,
  },
];
