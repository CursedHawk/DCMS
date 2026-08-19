import type { DcmsComponentSpec, TraitSpec } from '@dcms/gjs-schema';

/**
 * Composed page sections — the blocks most pages are actually built from.
 *
 * Each one drops real, finished markup rather than an empty shell, because the
 * fastest path to a decent page is editing something that already reads well.
 * They are ordinary elements with `dcms-*` classes, so an author can restyle any
 * of them from the Style panel or the code view without fighting a widget.
 *
 * Two things every section here shares, and they are what make a page look
 * designed rather than assembled:
 *
 *  - **A band control.** `data-tone`, `data-padding` and `data-width` are on the
 *    section itself, so alternating a page's bands — the single biggest visual
 *    difference between a template and a stack of blocks — is one dropdown and
 *    no CSS. On a coloured band the headings, links and muted text inherit, so
 *    nothing has to be corrected afterwards.
 *  - **Real inner parts.** The contents are the components from `parts.ts`
 *    (Card, Feature, PricingPlan, Stat, Step…), not anonymous markup. Dropping
 *    a fourth feature into a three-up grid is dragging a Feature, not copying a
 *    `<div>` in the layer tree and hoping its classes came along.
 */

const toneTrait: TraitSpec = {
  name: 'data-tone',
  label: 'Background',
  kind: 'select',
  default: 'default',
  description: 'Alternate this between neighbouring sections and the page reads as bands.',
  options: [
    { value: 'default', label: 'Page background' },
    { value: 'alt', label: 'Subtle' },
    { value: 'sunken', label: 'Sunken' },
    { value: 'brand-soft', label: 'Brand tint' },
    { value: 'brand', label: 'Brand' },
    { value: 'inverse', label: 'Dark' },
    { value: 'gradient', label: 'Gradient' },
  ],
};

const paddingTrait: TraitSpec = {
  name: 'data-padding',
  label: 'Vertical space',
  kind: 'select',
  default: 'default',
  options: [
    { value: 'none', label: 'None' },
    { value: 'sm', label: 'Tight' },
    { value: 'default', label: 'Normal' },
    { value: 'lg', label: 'Generous' },
  ],
};

const widthTrait: TraitSpec = {
  name: 'data-width',
  label: 'Content width',
  kind: 'select',
  default: 'default',
  options: [
    { value: 'narrow', label: 'Narrow — reading width' },
    { value: 'default', label: 'Normal' },
    { value: 'full', label: 'Edge to edge' },
  ],
};

/** The three controls every section band gets, in a consistent order. */
const bandTraits: TraitSpec[] = [toneTrait, paddingTrait, widthTrait];

const alignTrait: TraitSpec = {
  name: 'data-align',
  label: 'Align',
  kind: 'select',
  default: 'center',
  options: [
    { value: 'left', label: 'Left' },
    { value: 'center', label: 'Centre' },
    { value: 'right', label: 'Right' },
  ],
};

const columnsTrait = (fallback = 3): TraitSpec => ({
  name: 'data-columns',
  label: 'Columns',
  kind: 'select',
  default: String(fallback),
  description: 'Collapses to two on tablets and one on phones on its own.',
  options: [
    { value: '2', label: '2' },
    { value: '3', label: '3' },
    { value: '4', label: '4' },
    { value: '5', label: '5' },
    { value: '6', label: '6' },
  ],
});

const gapTrait: TraitSpec = {
  name: 'data-gap',
  label: 'Gap',
  kind: 'select',
  default: 'lg',
  options: [
    { value: 'sm', label: 'Small' },
    { value: 'md', label: 'Medium' },
    { value: 'lg', label: 'Large' },
    { value: 'xl', label: 'Extra large' },
  ],
};

/** The intro every content section opens with, so the markup stays consistent. */
const head = (eyebrow: string, heading: string, lead: string, align: 'left' | 'center' = 'left') =>
  `    <header class="dcms-section-head" data-align="${align}">
      <span class="dcms-eyebrow">${eyebrow}</span>
      <h2>${heading}</h2>
      <p class="dcms-section-lead">${lead}</p>
    </header>`;

const feature = (title: string, text: string) =>
  `      <article class="dcms-feature">
        <span class="dcms-icon" data-boxed="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 13l4 4L19 7"/></svg></span>
        <h3>${title}</h3>
        <p>${text}</p>
      </article>`;

const card = (meta: string, title: string, text: string) =>
  `      <article class="dcms-card" data-variant="outline" data-hover="lift">
        <img class="dcms-card-img" src="" alt="" loading="lazy" />
        <div class="dcms-card-body">
          <p class="dcms-card-meta">${meta}</p>
          <h3 class="dcms-card-title">${title}</h3>
          <p class="dcms-card-text">${text}</p>
          <div class="dcms-card-foot"><a class="dcms-button" data-variant="link" href="#">Read more</a></div>
        </div>
      </article>`;

const stat = (value: string, label: string) =>
  `      <div class="dcms-stat"><span class="dcms-stat-value">${value}</span><span class="dcms-stat-label">${label}</span></div>`;

const testimonial = (quote: string, name: string, role: string) =>
  `      <figure class="dcms-testimonial">
        <p>${quote}</p>
        <figcaption class="dcms-testimonial-author">
          <img src="" alt="" loading="lazy" />
          <span><span class="dcms-testimonial-name">${name}</span><span class="dcms-testimonial-role">${role}</span></span>
        </figcaption>
      </figure>`;

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
      {
        name: 'data-variant',
        label: 'Layout',
        kind: 'select',
        default: 'centered',
        options: [
          { value: 'centered', label: 'Centred' },
          { value: 'split', label: 'Text beside image' },
          { value: 'image-bg', label: 'Image background' },
          { value: 'gradient', label: 'Gradient' },
          { value: 'minimal', label: 'Minimal' },
          { value: 'full-height', label: 'Full height' },
        ],
      },
      alignTrait,
      {
        name: 'data-bg',
        label: 'Background image',
        kind: 'media',
        accepts: { mediaCategory: 'Image' },
        description: 'Used by the image-background layout; an overlay keeps the text readable.',
      },
      paddingTrait,
      widthTrait,
    ],
    snippet: `<section class="dcms-hero dcms-section" data-variant="centered" data-align="center">
  <div class="dcms-container">
    <span class="dcms-eyebrow">Now available</span>
    <h1 class="dcms-hero-title">A headline worth the space</h1>
    <p class="dcms-hero-text">One sentence explaining what this is, who it is for, and why they should keep reading.</p>
    <div class="dcms-hero-actions">
      <a class="dcms-button" data-size="lg" href="#">Get started</a>
      <a class="dcms-button" data-variant="ghost" data-size="lg" href="#">See how it works</a>
    </div>
  </div>
</section>`,
  },
  {
    type: 'HeroSplit',
    label: 'Hero with image',
    category: 'section',
    tag: 'section',
    // Deliberately not also `dcms-hero`: two components answering to the same
    // identity class means the parser has to guess between them, and a round trip
    // would silently turn one into the other. It borrows the hero's *inner*
    // classes (`dcms-hero-title`, `-text`, `-actions`), which carry the styling.
    identityClass: 'dcms-hero-split',
    icon: 'hero',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 1,
    docs: 'Headline and actions on one side, a product shot on the other.',
    traits: [
      { name: 'data-reverse', label: 'Image on the left', kind: 'checkbox' },
      paddingTrait,
      widthTrait,
    ],
    snippet: `<section class="dcms-hero-split dcms-section">
  <div class="dcms-container">
    <div class="dcms-row" data-gap="xl" data-align="center">
      <div class="dcms-col">
        <span class="dcms-eyebrow">Introducing</span>
        <h1 class="dcms-hero-title">Say the one thing that matters</h1>
        <p class="dcms-hero-text">Two short sentences. The first says what it is, the second says who it is for.</p>
        <div class="dcms-hero-actions">
          <a class="dcms-button" data-size="lg" href="#">Get started</a>
          <a class="dcms-button" data-variant="outline" data-size="lg" href="#">Book a demo</a>
        </div>
      </div>
      <div class="dcms-col">
        <img class="dcms-image" data-shape="wide" data-shadow="true" src="" alt="" loading="lazy" />
      </div>
    </div>
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
    order: 2,
    docs: 'Three or four short value propositions side by side.',
    traits: [
      {
        name: 'data-variant',
        label: 'Style',
        kind: 'select',
        default: 'plain',
        options: [
          { value: 'plain', label: 'Plain' },
          { value: 'cards', label: 'Cards' },
          { value: 'bordered', label: 'Edge rule' },
          { value: 'numbered', label: 'Numbered' },
        ],
      },
      columnsTrait(3),
      gapTrait,
      ...bandTraits,
    ],
    snippet: `<section class="dcms-feature-grid dcms-section" data-variant="cards" data-columns="3">
  <div class="dcms-container">
${head('Why us', 'Everything you need, nothing you don’t', 'One line that sets up the three claims below.')}
    <div class="dcms-grid" data-columns="3" data-gap="lg">
${feature('Fast to set up', 'Working in an afternoon, not a quarter — and without a migration project.')}
${feature('Yours to change', 'Every page is a real file you own, versioned in git like the rest of your work.')}
${feature('Grows with you', 'The same site handles ten pages and ten thousand without a rebuild.')}
    </div>
  </div>
</section>`,
  },
  {
    type: 'FeatureList',
    label: 'Alternating rows',
    category: 'section',
    tag: 'section',
    icon: 'features',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 3,
    docs: 'Alternating rows of text and image, for explaining a few things properly.',
    traits: [
      {
        name: 'data-alternate',
        label: 'Alternate sides',
        kind: 'checkbox',
        default: true,
        description: 'Every second row flips automatically — no per-row switch.',
      },
      ...bandTraits,
    ],
    snippet: `<section class="dcms-feature-list dcms-section" data-alternate="true">
  <div class="dcms-container">
${head('How it works', 'Two or three things, explained properly', 'Not a bullet list — the parts that need a paragraph.')}
    <div class="dcms-row" data-gap="xl" data-align="center">
      <div class="dcms-col">
        <h3>Explain one thing</h3>
        <p>Two or three sentences that earn their space. Say what happens, then say what it means for the reader.</p>
        <a class="dcms-button" data-variant="link" href="#">Read more</a>
      </div>
      <div class="dcms-col"><img class="dcms-image" data-shape="wide" src="" alt="" loading="lazy" /></div>
    </div>
    <div class="dcms-row" data-gap="xl" data-align="center">
      <div class="dcms-col">
        <h3>Then the next</h3>
        <p>The image side flips on its own, so the page keeps a rhythm without anyone maintaining it.</p>
        <a class="dcms-button" data-variant="link" href="#">Read more</a>
      </div>
      <div class="dcms-col"><img class="dcms-image" data-shape="wide" src="" alt="" loading="lazy" /></div>
    </div>
  </div>
</section>`,
  },
  {
    type: 'CardGrid',
    label: 'Card grid',
    category: 'section',
    tag: 'section',
    icon: 'card',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 4,
    docs: 'A grid of image cards — services, articles, case studies, anything with a picture.',
    traits: [columnsTrait(3), gapTrait, ...bandTraits],
    snippet: `<section class="dcms-card-grid dcms-section" data-columns="3">
  <div class="dcms-container">
${head('Our work', 'A grid of things worth clicking', 'One line about what these have in common.')}
    <div class="dcms-grid" data-columns="3" data-gap="lg">
${card('Category', 'A title that fits on two lines', 'One or two sentences of context, no more.')}
${card('Category', 'A second one, same shape', 'Consistency is what makes a grid read as a grid.')}
${card('Category', 'And a third', 'Three is usually the right number to start with.')}
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
    order: 5,
    docs: 'A single, unmissable next step.',
    traits: [
      {
        name: 'data-variant',
        label: 'Style',
        kind: 'select',
        default: 'default',
        options: [
          { value: 'default', label: 'Brand band' },
          { value: 'soft', label: 'Soft tint' },
          { value: 'split', label: 'Text left, button right' },
        ],
      },
      paddingTrait,
      widthTrait,
    ],
    snippet: `<section class="dcms-call-to-action dcms-section" data-variant="default">
  <div class="dcms-container">
    <div>
      <h2>Ready when you are</h2>
      <p>One line of reassurance — what happens after they click.</p>
    </div>
    <div class="dcms-hero-actions">
      <a class="dcms-button" data-size="lg" href="#">Start now</a>
    </div>
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
    order: 6,
    docs: 'Plans side by side, with one highlighted.',
    traits: [columnsTrait(3), gapTrait, ...bandTraits],
    snippet: `<section class="dcms-pricing-table dcms-section" data-columns="3">
  <div class="dcms-container">
${head('Pricing', 'Pick a plan, change it whenever', 'No line about contacting sales unless you mean it.', 'center')}
    <div class="dcms-grid" data-columns="3" data-gap="lg">
      <article class="dcms-plan">
        <h3>Starter</h3>
        <p class="dcms-plan-price">Free</p>
        <ul class="dcms-list" data-style="check"><li>The essentials</li><li>One project</li><li>Community support</li></ul>
        <a class="dcms-button" data-variant="outline" href="#">Choose</a>
      </article>
      <article class="dcms-plan" data-featured="true" data-featured-label="Popular">
        <h3>Team</h3>
        <p class="dcms-plan-price">€29<span>/month</span></p>
        <ul class="dcms-list" data-style="check"><li>Everything in Starter</li><li>Unlimited projects</li><li>The useful part</li></ul>
        <a class="dcms-button" href="#">Choose</a>
      </article>
      <article class="dcms-plan">
        <h3>Enterprise</h3>
        <p class="dcms-plan-price">Talk to us</p>
        <ul class="dcms-list" data-style="check"><li>Everything in Team</li><li>Support with a name on it</li></ul>
        <a class="dcms-button" data-variant="outline" href="#">Contact</a>
      </article>
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
    order: 7,
    docs: 'What people say, with a face and an attribution.',
    traits: [columnsTrait(3), gapTrait, ...bandTraits],
    snippet: `<section class="dcms-testimonials dcms-section" data-columns="3">
  <div class="dcms-container">
${head('Testimonials', 'In their words', 'Specific beats glowing. Keep the ones that name a number.', 'center')}
    <div class="dcms-grid" data-columns="3" data-gap="lg">
${testimonial('Something specific and believable, in their words rather than yours.', 'Name Surname', 'Role, Company')}
${testimonial('The best quotes name a number or a deadline. Those are the ones to keep.', 'Name Surname', 'Role, Company')}
${testimonial('Two sentences is plenty. Anything longer stops being read.', 'Name Surname', 'Role, Company')}
    </div>
  </div>
</section>`,
  },
  {
    type: 'QuoteBand',
    label: 'Pull quote',
    category: 'section',
    tag: 'section',
    icon: 'quote',
    classes: ['dcms-section'],
    acceptsChildren: true,
    order: 8,
    docs: 'One quote, large, on a band of its own.',
    traits: [...bandTraits],
    snippet: `<section class="dcms-quote-band dcms-section" data-tone="brand-soft" data-width="narrow">
  <div class="dcms-container">
    <blockquote class="dcms-blockquote" data-variant="large">
      <p>The one sentence you would want on a billboard.</p>
      <cite>Name Surname, Role at Company</cite>
    </blockquote>
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
    order: 9,
    docs: 'People, with photos and roles.',
    traits: [
      {
        name: 'data-variant',
        label: 'Photo shape',
        kind: 'select',
        default: 'default',
        options: [
          { value: 'default', label: 'Rounded' },
          { value: 'round', label: 'Circular' },
        ],
      },
      columnsTrait(4),
      gapTrait,
      ...bandTraits,
    ],
    snippet: `<section class="dcms-team-grid dcms-section" data-columns="4">
  <div class="dcms-container">
${head('The team', 'The people who will actually answer', 'One line about how the team works, not how big it is.', 'center')}
    <div class="dcms-grid" data-columns="4" data-gap="lg">
      <article class="dcms-person"><img src="" alt="" loading="lazy" /><h3>Name Surname</h3><p>Role</p></article>
      <article class="dcms-person"><img src="" alt="" loading="lazy" /><h3>Name Surname</h3><p>Role</p></article>
      <article class="dcms-person"><img src="" alt="" loading="lazy" /><h3>Name Surname</h3><p>Role</p></article>
      <article class="dcms-person"><img src="" alt="" loading="lazy" /><h3>Name Surname</h3><p>Role</p></article>
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
    order: 10,
    docs: 'A few numbers that make the case.',
    traits: [
      {
        name: 'data-variant',
        label: 'Style',
        kind: 'select',
        default: 'default',
        options: [
          { value: 'default', label: 'Plain' },
          { value: 'divided', label: 'Divided by rules' },
        ],
      },
      columnsTrait(4),
      ...bandTraits,
    ],
    snippet: `<section class="dcms-stats-band dcms-section" data-variant="divided" data-columns="4" data-tone="alt" data-padding="sm">
  <div class="dcms-container">
    <div class="dcms-grid" data-columns="4" data-gap="md">
${stat('12k', 'Members')}
${stat('99.9%', 'Uptime')}
${stat('24', 'Countries')}
${stat('4.8', 'Average rating')}
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
    order: 11,
    docs: 'A row of partner or customer logos.',
    traits: [
      {
        name: 'data-variant',
        label: 'Style',
        kind: 'select',
        default: 'default',
        options: [
          { value: 'default', label: 'As supplied' },
          { value: 'mono', label: 'Greyscale' },
        ],
      },
      paddingTrait,
      toneTrait,
    ],
    snippet: `<section class="dcms-logo-strip dcms-section" data-variant="mono" data-padding="sm">
  <div class="dcms-container">
    <p class="dcms-logo-strip-label">Trusted by</p>
    <div class="dcms-logo-strip-items">
      <img src="" alt="" loading="lazy" /><img src="" alt="" loading="lazy" /><img src="" alt="" loading="lazy" /><img src="" alt="" loading="lazy" /><img src="" alt="" loading="lazy" />
    </div>
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
    order: 12,
    docs: 'Questions and answers, collapsed by default.',
    traits: [...bandTraits],
    snippet: `<section class="dcms-faq dcms-section" data-width="narrow">
  <div class="dcms-container">
${head('FAQ', 'The questions you already get by email', 'Answer them in the first sentence, then explain.')}
    <div class="dcms-accordion">
      <details class="dcms-faq-item" open><summary>A question people actually ask</summary><p>A direct answer, in the first sentence.</p></details>
      <details class="dcms-faq-item"><summary>Another one</summary><p>A direct answer, in the first sentence.</p></details>
      <details class="dcms-faq-item"><summary>And the awkward one</summary><p>Answer it here rather than making them ask.</p></details>
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
    order: 13,
    docs: 'Events in order, with dates.',
    traits: [...bandTraits],
    snippet: `<section class="dcms-timeline dcms-section" data-width="narrow">
  <div class="dcms-container">
${head('History', 'How we got here', 'Three or four entries. A timeline nobody scrolls is a list.')}
    <ol class="dcms-timeline-items">
      <li class="dcms-timeline-item"><time datetime="2023">2023</time><h3>Something happened</h3><p>And this is what it meant.</p></li>
      <li class="dcms-timeline-item"><time datetime="2024">2024</time><h3>Something else</h3><p>And this is what it meant.</p></li>
      <li class="dcms-timeline-item"><time datetime="2025">2025</time><h3>And then this</h3><p>And this is what it meant.</p></li>
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
    order: 14,
    docs: 'A numbered process, in order. The numbers come from the markup order.',
    traits: [columnsTrait(3), gapTrait, ...bandTraits],
    snippet: `<section class="dcms-steps dcms-section" data-columns="3">
  <div class="dcms-container">
${head('Getting started', 'Three steps, then you are live', 'If it takes more than three, say so here.', 'center')}
    <ol class="dcms-grid" data-columns="3" data-gap="lg">
      <li class="dcms-step"><h3>Sign up</h3><p>What happens first, and how long it takes.</p></li>
      <li class="dcms-step"><h3>Set it up</h3><p>What happens next, and what you will need to hand.</p></li>
      <li class="dcms-step"><h3>Go live</h3><p>And the result, stated as the reader would state it.</p></li>
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
    order: 15,
    docs: 'A feature-by-feature table that scrolls sideways rather than squashing on a phone.',
    traits: [
      {
        name: 'data-variant',
        label: 'Rows',
        kind: 'select',
        default: 'default',
        options: [
          { value: 'default', label: 'Plain' },
          { value: 'striped', label: 'Striped' },
        ],
      },
      ...bandTraits,
    ],
    snippet: `<section class="dcms-comparison-table dcms-section">
  <div class="dcms-container">
${head('Compare', 'What is in each plan', 'The row people are looking for should be near the top.')}
    <div class="dcms-table-scroll">
      <table class="dcms-table" data-variant="striped">
        <thead><tr><th>Feature</th><th>Starter</th><th>Team</th><th>Enterprise</th></tr></thead>
        <tbody>
          <tr><td>The basics</td><td>Yes</td><td>Yes</td><td>Yes</td></tr>
          <tr><td>The useful part</td><td>—</td><td>Yes</td><td>Yes</td></tr>
          <tr><td>Support with a name on it</td><td>—</td><td>—</td><td>Yes</td></tr>
        </tbody>
      </table>
    </div>
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
    order: 16,
    docs: 'Address, phone and email beside a map or a form.',
    traits: [...bandTraits],
    snippet: `<section class="dcms-contact-block dcms-section">
  <div class="dcms-container">
    <div class="dcms-row" data-gap="xl" data-align="start">
      <div class="dcms-col">
${head('Contact', 'Get in touch', 'Say who picks these up and how quickly.')}
        <address class="dcms-contact-details">
          <p>Street 1, City</p>
          <p><a href="tel:+420000000000">+420 000 000 000</a></p>
          <p><a href="mailto:hello@example.com">hello@example.com</a></p>
        </address>
      </div>
      <div class="dcms-col"><div class="dcms-map-embed" data-query="" data-zoom="14"></div></div>
    </div>
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
    order: 17,
    docs: 'An embedded map for a single address.',
    traits: [
      { name: 'data-query', label: 'Address', kind: 'text', default: '' },
      { name: 'data-zoom', label: 'Zoom', kind: 'number', default: 14 },
    ],
    snippet: `<div class="dcms-map-embed" data-query="" data-zoom="14"></div>`,
  },
];
