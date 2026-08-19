import type { DcmsComponentSpec, TraitSpec } from '@dcms/gjs-schema';

/**
 * The repeatable pieces sections are built from.
 *
 * A section drops a finished band — three features, three plans, four stats.
 * The very next thing an author wants is a fourth one, and before this file that
 * meant selecting an anonymous `<article>` in the layer tree, copying it, and
 * hoping the classes came along. Each piece here is a real component instead:
 * it has its own palette tile, its own traits, and its own identity class, so it
 * can be dragged into any grid, duplicated, and configured on its own.
 *
 * Every class used below is styled by `BLOCKS_CSS` purely from theme tokens, so
 * a piece dropped into a page looks like the rest of the site immediately — the
 * markup carries the structure and the design kit carries the look.
 */

const alignTrait: TraitSpec = {
  name: 'data-align',
  label: 'Align',
  kind: 'select',
  default: 'left',
  options: [
    { value: 'left', label: 'Left' },
    { value: 'center', label: 'Centre' },
  ],
};

export const partSpecs: DcmsComponentSpec[] = [
  {
    type: 'SectionHead',
    label: 'Section intro',
    category: 'part',
    tag: 'header',
    icon: 'eyebrow',
    acceptsChildren: true,
    order: 0,
    docs: 'The eyebrow, heading and one supporting line that opens a section.',
    traits: [alignTrait],
    snippet: `<header class="dcms-section-head" data-align="left">
  <span class="dcms-eyebrow">Why us</span>
  <h2>A heading that says what this section is</h2>
  <p class="dcms-section-lead">One supporting sentence. Not a paragraph — a sentence.</p>
</header>`,
  },
  {
    type: 'Eyebrow',
    label: 'Eyebrow',
    category: 'part',
    tag: 'span',
    icon: 'eyebrow',
    acceptsChildren: true,
    order: 1,
    docs: 'The small brand-coloured label above a heading.',
    traits: [],
    snippet: `<span class="dcms-eyebrow">Label</span>`,
  },
  {
    type: 'Card',
    label: 'Card',
    category: 'part',
    tag: 'article',
    icon: 'card',
    acceptsChildren: true,
    order: 2,
    docs: 'Image, title, a line of text and a link — the piece most grids are made of.',
    traits: [
      {
        name: 'data-variant',
        label: 'Style',
        kind: 'select',
        default: 'outline',
        description: 'How the card separates itself from the page behind it.',
        options: [
          { value: 'outline', label: 'Outlined' },
          { value: 'raised', label: 'Raised' },
          { value: 'soft', label: 'Soft fill' },
          { value: 'plain', label: 'No box' },
        ],
      },
      {
        name: 'data-hover',
        label: 'On hover',
        kind: 'select',
        default: 'lift',
        options: [
          { value: 'none', label: 'Nothing' },
          { value: 'lift', label: 'Lift' },
          { value: 'border', label: 'Highlight edge' },
        ],
      },
    ],
    snippet: `<article class="dcms-card" data-variant="outline" data-hover="lift">
  <img class="dcms-card-img" src="" alt="" loading="lazy" />
  <div class="dcms-card-body">
    <p class="dcms-card-meta">Category</p>
    <h3 class="dcms-card-title">A title that fits on two lines</h3>
    <p class="dcms-card-text">One or two sentences of context, no more.</p>
    <div class="dcms-card-foot"><a class="dcms-button" data-variant="link" href="#">Read more</a></div>
  </div>
</article>`,
  },
  {
    type: 'Feature',
    label: 'Feature',
    category: 'part',
    tag: 'article',
    icon: 'features',
    acceptsChildren: true,
    order: 3,
    docs: 'One benefit: an icon, a short title and a sentence that makes it concrete.',
    traits: [],
    snippet: `<article class="dcms-feature">
  <span class="dcms-icon" data-boxed="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 13l4 4L19 7"/></svg></span>
  <h3>A benefit, stated plainly</h3>
  <p>A sentence that makes the benefit concrete rather than impressive.</p>
</article>`,
  },
  {
    type: 'PricingPlan',
    label: 'Pricing plan',
    category: 'part',
    tag: 'article',
    identityClass: 'dcms-plan',
    icon: 'plan',
    acceptsChildren: true,
    order: 4,
    docs: 'One column of a pricing table, optionally the highlighted one.',
    traits: [
      {
        name: 'data-featured',
        label: 'Highlight this plan',
        kind: 'checkbox',
        description: 'Draws the brand border and shows the ribbon label.',
      },
      {
        name: 'data-featured-label',
        label: 'Ribbon text',
        kind: 'text',
        default: 'Popular',
        description: 'Only shown while the plan is highlighted.',
      },
    ],
    snippet: `<article class="dcms-plan" data-featured-label="Popular">
  <h3>Starter</h3>
  <p class="dcms-plan-price">Free<span>/month</span></p>
  <ul class="dcms-list" data-style="check">
    <li>The essentials</li>
    <li>And the one thing people ask about</li>
  </ul>
  <a class="dcms-button" data-variant="outline" href="#">Choose</a>
</article>`,
  },
  {
    type: 'TeamMember',
    label: 'Person',
    category: 'part',
    tag: 'article',
    identityClass: 'dcms-person',
    icon: 'person',
    acceptsChildren: true,
    order: 5,
    docs: 'A photo, a name and a role.',
    traits: [],
    snippet: `<article class="dcms-person">
  <img src="" alt="" loading="lazy" />
  <h3>Name Surname</h3>
  <p>Role</p>
</article>`,
  },
  {
    type: 'Stat',
    label: 'Statistic',
    category: 'part',
    tag: 'div',
    icon: 'stat',
    acceptsChildren: true,
    order: 6,
    docs: 'One number and what it counts.',
    traits: [],
    snippet: `<div class="dcms-stat">
  <span class="dcms-stat-value">12k</span>
  <span class="dcms-stat-label">Members</span>
</div>`,
  },
  {
    type: 'Testimonial',
    label: 'Testimonial',
    category: 'part',
    tag: 'figure',
    icon: 'testimonial',
    acceptsChildren: true,
    order: 7,
    docs: 'A quote with a face and an attribution.',
    traits: [],
    snippet: `<figure class="dcms-testimonial">
  <p>Something specific and believable, in their words rather than yours.</p>
  <figcaption class="dcms-testimonial-author">
    <img src="" alt="" loading="lazy" />
    <span><span class="dcms-testimonial-name">Name Surname</span><span class="dcms-testimonial-role">Role, Company</span></span>
  </figcaption>
</figure>`,
  },
  {
    type: 'Step',
    label: 'Step',
    category: 'part',
    tag: 'li',
    icon: 'step',
    acceptsChildren: true,
    order: 8,
    docs: 'One step of a process. Numbering comes from the order on the page.',
    traits: [],
    snippet: `<li class="dcms-step">
  <h3>What happens</h3>
  <p>And what the reader has to do about it.</p>
</li>`,
  },
  {
    type: 'TimelineItem',
    label: 'Timeline entry',
    category: 'part',
    tag: 'li',
    icon: 'timeline',
    acceptsChildren: true,
    order: 9,
    docs: 'One dated event on a timeline.',
    traits: [],
    snippet: `<li class="dcms-timeline-item">
  <time datetime="2025">2025</time>
  <h3>Something happened</h3>
  <p>And this is what it meant.</p>
</li>`,
  },
  {
    type: 'FaqItem',
    label: 'Question',
    category: 'part',
    tag: 'details',
    icon: 'faq',
    acceptsChildren: true,
    order: 10,
    docs: 'One question and its answer, collapsed until opened.',
    traits: [
      {
        name: 'open',
        label: 'Open by default',
        kind: 'checkbox',
        description: 'Leave the first question open so the pattern is obvious.',
      },
    ],
    snippet: `<details class="dcms-faq-item">
  <summary>A question people actually ask</summary>
  <p>A direct answer, in the first sentence.</p>
</details>`,
  },
  {
    type: 'Slide',
    label: 'Slide',
    category: 'part',
    tag: 'div',
    icon: 'slide',
    acceptsChildren: true,
    order: 11,
    docs: 'One panel of a carousel.',
    traits: [],
    snippet: `<div class="dcms-slide">
  <img class="dcms-image" data-shape="wide" src="" alt="" loading="lazy" />
</div>`,
  },
  {
    type: 'Actions',
    label: 'Button row',
    category: 'part',
    tag: 'div',
    identityClass: 'dcms-hero-actions',
    icon: 'button',
    acceptsChildren: true,
    order: 12,
    docs: 'A primary and a secondary action, side by side and wrapping on small screens.',
    traits: [],
    snippet: `<div class="dcms-hero-actions">
  <a class="dcms-button" href="#">Get started</a>
  <a class="dcms-button" data-variant="ghost" href="#">Talk to us</a>
</div>`,
  },
];
