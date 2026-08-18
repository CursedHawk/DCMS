import type { DcmsComponentSpec } from '@dcms/gjs-schema';

/**
 * Composed page sections — the blocks most pages are actually built from.
 *
 * Each one drops real, finished markup rather than an empty shell, because the
 * fastest path to a decent page is editing something that already reads well.
 * They are ordinary elements with `dcms-*` classes, so an author can restyle any
 * of them from the Style panel or the code view without fighting a widget.
 */

const alignTrait = {
  name: 'data-align',
  label: 'Align',
  kind: 'select' as const,
  default: 'center',
  options: [
    { value: 'left', label: 'Left' },
    { value: 'center', label: 'Centre' },
    { value: 'right', label: 'Right' },
  ],
};

const columnsTrait = (fallback = 3) => ({
  name: 'data-columns',
  label: 'Columns',
  kind: 'number' as const,
  default: fallback,
});

export const sectionSpecs: DcmsComponentSpec[] = [
  {
    type: 'Hero',
    label: 'Hero',
    category: 'section',
    tag: 'section',
    icon: 'hero',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 0,
    docs: 'The opening statement of a page: headline, supporting line and a call to action.',
    traits: [
      alignTrait,
      {
        name: 'data-variant',
        label: 'Variant',
        kind: 'select',
        default: 'centered',
        options: [
          { value: 'centered', label: 'Centred' },
          { value: 'split', label: 'Text and image' },
          { value: 'image-bg', label: 'Image background' },
          { value: 'minimal', label: 'Minimal' },
          { value: 'full-height', label: 'Full height' },
        ],
      },
      { name: 'data-bg', label: 'Background image', kind: 'media', accepts: { mediaCategory: 'Image' } },
    ],
    snippet: `<section class="dcms-hero dcms-section" data-variant="centered" data-align="center">
  <div class="dcms-container">
    <h1 class="dcms-hero-title">A headline worth the space</h1>
    <p class="dcms-hero-text">One sentence explaining what this is and who it is for.</p>
    <a class="dcms-button" href="#">Get started</a>
  </div>
</section>`,
  },
  {
    type: 'FeatureGrid',
    label: 'Feature grid',
    category: 'section',
    tag: 'section',
    icon: 'features',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 1,
    docs: 'Three or four short value propositions side by side.',
    traits: [columnsTrait(3)],
    snippet: `<section class="dcms-feature-grid dcms-section" data-columns="3">
  <div class="dcms-container">
    <h2>What you get</h2>
    <div class="dcms-grid" data-columns="3" data-gap="lg">
      <article class="dcms-feature"><h3>First benefit</h3><p>A sentence that makes the benefit concrete.</p></article>
      <article class="dcms-feature"><h3>Second benefit</h3><p>A sentence that makes the benefit concrete.</p></article>
      <article class="dcms-feature"><h3>Third benefit</h3><p>A sentence that makes the benefit concrete.</p></article>
    </div>
  </div>
</section>`,
  },
  {
    type: 'FeatureList',
    label: 'Feature list',
    category: 'section',
    tag: 'section',
    icon: 'features',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 2,
    docs: 'Alternating rows of text and image, for explaining a few things properly.',
    traits: [],
    snippet: `<section class="dcms-feature-list dcms-section">
  <div class="dcms-container">
    <div class="dcms-row" data-gap="lg" data-align="center">
      <div class="dcms-col"><h3>Explain one thing</h3><p>Two or three sentences, not a bullet list.</p></div>
      <div class="dcms-col"><img class="dcms-image" src="" alt="" loading="lazy" /></div>
    </div>
  </div>
</section>`,
  },
  {
    type: 'CallToAction',
    label: 'Call to action',
    category: 'section',
    tag: 'section',
    icon: 'cta',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 3,
    docs: 'A single, unmissable next step.',
    traits: [alignTrait],
    snippet: `<section class="dcms-call-to-action dcms-section" data-align="center">
  <div class="dcms-container">
    <h2>Ready when you are</h2>
    <p>One line of reassurance.</p>
    <a class="dcms-button" href="#">Start now</a>
  </div>
</section>`,
  },
  {
    type: 'PricingTable',
    label: 'Pricing',
    category: 'section',
    tag: 'section',
    icon: 'pricing',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 4,
    docs: 'Plans side by side, with one highlighted.',
    traits: [columnsTrait(3)],
    snippet: `<section class="dcms-pricing-table dcms-section" data-columns="3">
  <div class="dcms-container">
    <h2>Pricing</h2>
    <div class="dcms-grid" data-columns="3" data-gap="lg">
      <article class="dcms-plan"><h3>Starter</h3><p class="dcms-plan-price">Free</p><ul class="dcms-list"><li>The essentials</li></ul><a class="dcms-button" href="#">Choose</a></article>
      <article class="dcms-plan" data-featured="true"><h3>Team</h3><p class="dcms-plan-price">€29<span>/month</span></p><ul class="dcms-list"><li>Everything in Starter</li><li>And the useful part</li></ul><a class="dcms-button" href="#">Choose</a></article>
      <article class="dcms-plan"><h3>Enterprise</h3><p class="dcms-plan-price">Talk to us</p><ul class="dcms-list"><li>Everything, plus support</li></ul><a class="dcms-button" href="#">Contact</a></article>
    </div>
  </div>
</section>`,
  },
  {
    type: 'Testimonials',
    label: 'Testimonials',
    category: 'section',
    tag: 'section',
    icon: 'testimonial',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 5,
    docs: 'What people say, with attribution.',
    traits: [columnsTrait(2)],
    snippet: `<section class="dcms-testimonials dcms-section" data-columns="2">
  <div class="dcms-container">
    <h2>What people say</h2>
    <div class="dcms-grid" data-columns="2" data-gap="lg">
      <blockquote class="dcms-blockquote"><p>Something specific and believable.</p><cite>Name, Role</cite></blockquote>
      <blockquote class="dcms-blockquote"><p>Something specific and believable.</p><cite>Name, Role</cite></blockquote>
    </div>
  </div>
</section>`,
  },
  {
    type: 'TeamGrid',
    label: 'Team',
    category: 'section',
    tag: 'section',
    icon: 'team',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 6,
    docs: 'People, with photos and roles.',
    traits: [columnsTrait(4)],
    snippet: `<section class="dcms-team-grid dcms-section" data-columns="4">
  <div class="dcms-container">
    <h2>The team</h2>
    <div class="dcms-grid" data-columns="4" data-gap="lg">
      <article class="dcms-person"><img class="dcms-image" src="" alt="" loading="lazy" /><h3>Name</h3><p>Role</p></article>
      <article class="dcms-person"><img class="dcms-image" src="" alt="" loading="lazy" /><h3>Name</h3><p>Role</p></article>
    </div>
  </div>
</section>`,
  },
  {
    type: 'StatsBand',
    label: 'Stats',
    category: 'section',
    tag: 'section',
    icon: 'stats',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 7,
    docs: 'A few numbers that make the case.',
    traits: [columnsTrait(4)],
    snippet: `<section class="dcms-stats-band dcms-section" data-columns="4">
  <div class="dcms-container">
    <div class="dcms-grid" data-columns="4" data-gap="md">
      <div class="dcms-stat"><span class="dcms-stat-value">12k</span><span class="dcms-stat-label">Members</span></div>
      <div class="dcms-stat"><span class="dcms-stat-value">99.9%</span><span class="dcms-stat-label">Uptime</span></div>
      <div class="dcms-stat"><span class="dcms-stat-value">24</span><span class="dcms-stat-label">Countries</span></div>
      <div class="dcms-stat"><span class="dcms-stat-value">4.8</span><span class="dcms-stat-label">Rating</span></div>
    </div>
  </div>
</section>`,
  },
  {
    type: 'LogoStrip',
    label: 'Logo strip',
    category: 'section',
    tag: 'section',
    icon: 'logos',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 8,
    docs: 'A row of partner or customer logos.',
    traits: [],
    snippet: `<section class="dcms-logo-strip dcms-section">
  <div class="dcms-container">
    <p class="dcms-logo-strip-label">Trusted by</p>
    <div class="dcms-logo-strip-items"><img src="" alt="" loading="lazy" /><img src="" alt="" loading="lazy" /><img src="" alt="" loading="lazy" /></div>
  </div>
</section>`,
  },
  {
    type: 'Faq',
    label: 'FAQ',
    category: 'section',
    tag: 'section',
    icon: 'faq',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 9,
    docs: 'Questions and answers, collapsed by default.',
    traits: [],
    snippet: `<section class="dcms-faq dcms-section">
  <div class="dcms-container">
    <h2>Frequently asked</h2>
    <div class="dcms-accordion">
      <details open><summary>A question people actually ask</summary><p>A direct answer.</p></details>
      <details><summary>Another one</summary><p>A direct answer.</p></details>
    </div>
  </div>
</section>`,
  },
  {
    type: 'Timeline',
    label: 'Timeline',
    category: 'section',
    tag: 'section',
    icon: 'timeline',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 10,
    docs: 'Events in order, with dates.',
    traits: [],
    snippet: `<section class="dcms-timeline dcms-section">
  <div class="dcms-container">
    <h2>How we got here</h2>
    <ol class="dcms-timeline-items">
      <li><time datetime="2024">2024</time><h3>Something happened</h3><p>And this is what it meant.</p></li>
      <li><time datetime="2025">2025</time><h3>Something else</h3><p>And this is what it meant.</p></li>
    </ol>
  </div>
</section>`,
  },
  {
    type: 'Steps',
    label: 'Steps',
    category: 'section',
    tag: 'section',
    icon: 'steps',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 11,
    docs: 'A numbered process, in order.',
    traits: [columnsTrait(3)],
    snippet: `<section class="dcms-steps dcms-section" data-columns="3">
  <div class="dcms-container">
    <h2>How it works</h2>
    <ol class="dcms-grid" data-columns="3" data-gap="lg">
      <li class="dcms-step"><h3>Sign up</h3><p>What happens first.</p></li>
      <li class="dcms-step"><h3>Set it up</h3><p>What happens next.</p></li>
      <li class="dcms-step"><h3>Go live</h3><p>And the result.</p></li>
    </ol>
  </div>
</section>`,
  },
  {
    type: 'ComparisonTable',
    label: 'Comparison',
    category: 'section',
    tag: 'section',
    icon: 'comparison',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 12,
    docs: 'A feature-by-feature table.',
    traits: [],
    snippet: `<section class="dcms-comparison-table dcms-section">
  <div class="dcms-container">
    <table class="dcms-table">
      <thead><tr><th>Feature</th><th>Starter</th><th>Team</th></tr></thead>
      <tbody>
        <tr><td>The basics</td><td>Yes</td><td>Yes</td></tr>
        <tr><td>The useful part</td><td>No</td><td>Yes</td></tr>
      </tbody>
    </table>
  </div>
</section>`,
  },
  {
    type: 'ContactBlock',
    label: 'Contact',
    category: 'section',
    tag: 'section',
    icon: 'contact',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 13,
    docs: 'Address, phone and email, laid out to be scanned.',
    traits: [],
    snippet: `<section class="dcms-contact-block dcms-section">
  <div class="dcms-container">
    <h2>Get in touch</h2>
    <address class="dcms-contact-details">
      <p>Street 1, City</p>
      <p><a href="tel:+420000000000">+420 000 000 000</a></p>
      <p><a href="mailto:hello@example.com">hello@example.com</a></p>
    </address>
  </div>
</section>`,
  },
  {
    type: 'MapEmbed',
    label: 'Map',
    category: 'section',
    tag: 'div',
    icon: 'map',
    acceptsChildren: false,
    order: 14,
    docs: 'An embedded map for a single address.',
    traits: [
      { name: 'data-query', label: 'Address', kind: 'text', default: '' },
      { name: 'data-zoom', label: 'Zoom', kind: 'number', default: 14 },
    ],
    snippet: `<div class="dcms-map-embed" data-query="" data-zoom="14"></div>`,
  },
];
