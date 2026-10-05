import type { StarterNode } from '@dcms/site-runtime';

/**
 * The section library (Mode D v2, U2): finished bands of a page — heroes, features, pricing,
 * FAQ… — as ordinary trees of built-in components. Inserting one gives the author editable
 * parts, not a sealed widget: every heading, card and button is theirs to change, move or
 * delete. Real copy, so the page reads well before anything is edited.
 *
 * Admin-side data only: a site never ships the library, only what was inserted from it.
 */
export interface SectionTemplate {
  id: string;
  label: string;
  category: string;
  description: string;
  keywords: readonly string[];
  tree: StarterNode;
}

// --- Small builders, so a template reads as its layout -----------------------------------

type Props = Record<string, unknown>;
const node = (type: string, props: Props = {}, slots?: Record<string, StarterNode[]>): StarterNode => ({ type, props, ...(slots ? { slots } : {}) });
const section = (props: Props, children: StarterNode[]) => node('dcms.section', props, { default: children });
const stack = (props: Props, children: StarterNode[]) => node('dcms.stack', props, { default: children });
const grid = (columns: string, children: StarterNode[], props: Props = {}) => node('dcms.grid', { columns, gap: 'lg', ...props }, { default: children });
const split = (ratio: string, start: StarterNode[], end: StarterNode[]) => node('dcms.split', { ratio }, { start, end });
const h = (text: string, level = '2', props: Props = {}) => node('dcms.heading', { text, level, ...props });
const p = (text: string, props: Props = {}) => node('dcms.text', { text, ...props });
const btn = (label: string, variant = 'primary', props: Props = {}) => node('dcms.button', { label, variant, ...props });
const img = (alt: string, props: Props = {}) => node('dcms.image', { alt, ratio: '4-3', radius: 'lg', ...props });
const icon = (name: string, props: Props = {}) => node('dcms.icon', { icon: name, shape: 'circle', ...props });
const badge = (text: string, tone = 'brand') => node('dcms.badge', { text, tone });
const card = (props: Props, content: StarterNode[], media: StarterNode[] = [], footer: StarterNode[] = []) =>
  node('dcms.card', props, { media, default: content, footer });
const centred = (...children: StarterNode[]) => stack({ align: 'center', gap: 'sm' }, children);
const intro = (eyebrow: string, title: string, text: string) =>
  centred(badge(eyebrow), h(title, '2', { align: 'center' }), p(text, { align: 'center', tone: 'muted' }));

const featureCard = (iconName: string, title: string, text: string) =>
  card({ variant: 'plain', padding: 'sm' }, [icon(iconName), h(title, '3'), p(text, { tone: 'muted' })]);

// --- The library -------------------------------------------------------------------------

export const SECTION_TEMPLATES: readonly SectionTemplate[] = [
  // Headers
  {
    id: 'header-simple',
    label: 'Header',
    category: 'Headers',
    description: 'Your name on the left, the main menu on the right. Put it in the app shell.',
    keywords: ['navbar', 'top', 'menu', 'logo'],
    tree: section({ spacing: 'sm' }, [
      stack({ direction: 'horizontal', justify: 'between', align: 'center' }, [h('Your company', '4'), node('dcms.nav', { menu: 'main' })]),
    ]),
  },
  {
    id: 'header-cta',
    label: 'Header with button',
    category: 'Headers',
    description: 'Name, menu and one call to action — “Book now”, “Get in touch”.',
    keywords: ['navbar', 'top', 'menu', 'cta'],
    tree: section({ spacing: 'sm' }, [
      stack({ direction: 'horizontal', justify: 'between', align: 'center' }, [
        h('Your company', '4'),
        stack({ direction: 'horizontal', align: 'center', gap: 'lg' }, [node('dcms.nav', { menu: 'main' }), btn('Get in touch', 'primary', { size: 'sm' })]),
      ]),
    ]),
  },

  // Heroes
  {
    id: 'hero-centred',
    label: 'Hero',
    category: 'Heroes',
    description: 'A big centred headline, a sentence and two buttons — the top of a home page.',
    keywords: ['banner', 'headline', 'intro', 'landing', 'top'],
    tree: section({ background: 'soft', spacing: 'lg' }, [
      stack({ align: 'center', gap: 'md' }, [
        badge('New this season'),
        h('Make something people love', '1', { align: 'center' }),
        p('A sentence or two on what you offer and who it is for. Keep it short — the page says the rest.', { align: 'center', size: 'lg', tone: 'muted' }),
        stack({ direction: 'horizontal', gap: 'sm', justify: 'center' }, [btn('Get started'), btn('Learn more', 'secondary')]),
      ]),
    ]),
  },
  {
    id: 'hero-split',
    label: 'Hero with picture',
    category: 'Heroes',
    description: 'Headline and buttons on one side, a picture on the other. Stacks on phones.',
    keywords: ['banner', 'headline', 'image', 'landing'],
    tree: section({ spacing: 'lg' }, [
      split(
        '1-1',
        [
          stack({ gap: 'md', justify: 'center' }, [
            h('Everything you need, in one place', '1'),
            p('Explain the benefit in plain words. Visitors decide in seconds whether to read on.', { size: 'lg', tone: 'muted' }),
            stack({ direction: 'horizontal', gap: 'sm' }, [btn('Start now'), btn('See how it works', 'ghost')]),
          ]),
        ],
        [img('A picture that shows what you do', { ratio: '4-3' })],
      ),
    ]),
  },
  {
    id: 'hero-photo',
    label: 'Hero on a photo',
    category: 'Heroes',
    description: 'White headline over a full-width photo, darkened so it stays readable.',
    keywords: ['banner', 'background image', 'photo', 'cover'],
    tree: section({ spacing: 'lg', background: 'inverse', overlay: 'dark' }, [
      stack({ align: 'center', gap: 'md' }, [
        h('Seen from a new angle', '1', { align: 'center' }),
        p('Choose a background picture in this section’s settings.', { align: 'center', size: 'lg' }),
        btn('Explore'),
      ]),
    ]),
  },

  // Features
  {
    id: 'features-grid',
    label: 'Feature grid',
    category: 'Features',
    description: 'Three strengths, each with an icon, a title and a sentence.',
    keywords: ['benefits', 'services', 'why us', 'icons'],
    tree: section({}, [
      intro('Why us', 'Built around what matters', 'Three reasons people choose us — and keep coming back.'),
      grid('3', [
        featureCard('zap', 'Fast', 'Things happen quickly, without you having to chase them.'),
        featureCard('shield-check', 'Reliable', 'It works the same way every time, for everyone.'),
        featureCard('smile', 'Friendly', 'Real people answer, in plain language.'),
      ], { columns: '3' }),
    ]),
  },
  {
    id: 'features-rows',
    label: 'Alternating rows',
    category: 'Features',
    description: 'A picture beside each point, swapping sides from row to row.',
    keywords: ['zigzag', 'image and text', 'story', 'features'],
    tree: section({}, [
      stack({ gap: 'xl' }, [
        split('1-1', [stack({ justify: 'center', gap: 'sm' }, [badge('Step one'), h('Start where you are', '3'), p('Describe the first thing people get from you, and why it helps.', { tone: 'muted' })])], [img('First feature')]),
        split('1-1', [img('Second feature')], [stack({ justify: 'center', gap: 'sm' }, [badge('Step two'), h('Grow with confidence', '3'), p('Then the next — each row one idea, each picture showing it.', { tone: 'muted' })])]),
      ]),
    ]),
  },
  {
    id: 'features-checklist',
    label: 'Checklist',
    category: 'Features',
    description: 'A heading and a sentence beside a list of ticks — what is included.',
    keywords: ['included', 'list', 'benefits', 'ticks'],
    tree: section({ background: 'alt' }, [
      split('1-1', [stack({ gap: 'sm' }, [h('Everything included', '2'), p('No extras, no surprises. Here is what you get from day one.', { tone: 'muted' })])], [
        node('dcms.list', { items: 'Unlimited projects\nFriendly support by email and phone\nFree updates for life\nCancel any time', marker: 'check', gap: 'md' }),
      ]),
    ]),
  },
  {
    id: 'services-cards',
    label: 'Services',
    category: 'Features',
    description: 'Cards for what you offer, each with an icon, a description and a link.',
    keywords: ['offer', 'products', 'cards', 'what we do'],
    tree: section({}, [
      intro('Services', 'What we do', 'Pick what you need — or talk to us about something else.'),
      grid('3', [
        card({ variant: 'outline' }, [icon('palette'), h('Design', '3'), p('Brands, sites and print that look like you.', { tone: 'muted' })], [], [btn('Learn more', 'ghost', { size: 'sm' })]),
        card({ variant: 'outline' }, [icon('code'), h('Development', '3'), p('Web apps that are fast, safe and easy to change.', { tone: 'muted' })], [], [btn('Learn more', 'ghost', { size: 'sm' })]),
        card({ variant: 'outline' }, [icon('trending-up'), h('Growth', '3'), p('More of the right visitors, and more of them staying.', { tone: 'muted' })], [], [btn('Learn more', 'ghost', { size: 'sm' })]),
      ]),
    ]),
  },

  // Content
  {
    id: 'cards-grid',
    label: 'Card grid',
    category: 'Content',
    description: 'Three cards with a picture, a title, a sentence and a button.',
    keywords: ['articles', 'posts', 'products', 'tiles'],
    tree: section({}, [
      h('Latest from us', '2'),
      grid('3', [1, 2, 3].map((n) => card({ variant: 'raised' }, [h(`Card title ${n}`, '3'), p('A short teaser that makes people want to read more.', { tone: 'muted' })], [img(`Picture ${n}`, { ratio: '16-9', radius: 'none' })], [btn('Read more', 'ghost', { size: 'sm' })]))),
    ]),
  },
  {
    id: 'text-image',
    label: 'Text and picture',
    category: 'Content',
    description: 'A heading and paragraphs beside a picture — about us, a story, a detail.',
    keywords: ['about', 'story', 'image and text'],
    tree: section({}, [
      split('1-1', [stack({ gap: 'sm', justify: 'center' }, [h('Our story', '2'), p('Tell people where you started and what drives you. Two short paragraphs are plenty.'), p('People buy from people: a name, a place and a reason go a long way.', { tone: 'muted' })])], [img('A photo of your team or place')]),
    ]),
  },
  {
    id: 'video-section',
    label: 'Video',
    category: 'Media',
    description: 'A heading and a sentence above a widescreen video.',
    keywords: ['youtube', 'film', 'watch'],
    tree: section({}, [
      stack({ align: 'center', gap: 'md' }, [h('See it in action', '2', { align: 'center' }), p('Two minutes that explain it better than any page could.', { align: 'center', tone: 'muted' })]),
      node('dcms.video', { title: 'Introduction video' }),
    ]),
  },
  {
    id: 'gallery-section',
    label: 'Photo gallery',
    category: 'Media',
    description: 'A heading and a grid of photos that open full-screen.',
    keywords: ['photos', 'portfolio', 'pictures', 'lightbox'],
    tree: section({}, [h('Gallery', '2'), node('dcms.gallery', { columns: '3' }, { images: [1, 2, 3, 4, 5, 6].map((n) => img(`Photo ${n}`, { ratio: 'auto', radius: 'none' })) })]),
  },

  // Social proof
  {
    id: 'testimonials',
    label: 'Testimonials',
    category: 'Social proof',
    description: 'Three short quotes from happy customers.',
    keywords: ['reviews', 'quotes', 'customers', 'feedback'],
    tree: section({ background: 'alt' }, [
      intro('Testimonials', 'People like working with us', 'Don’t take our word for it.'),
      grid('3', [
        node('dcms.quote', { text: 'They understood what we needed before we did.', cite: 'Jana, café owner', style: 'card' }),
        node('dcms.quote', { text: 'Fast, friendly and fair — exactly as promised.', cite: 'Tomáš, architect', style: 'card' }),
        node('dcms.quote', { text: 'Our customers noticed the difference in a week.', cite: 'Eva, shop manager', style: 'card' }),
      ]),
    ]),
  },
  {
    id: 'quote-band',
    label: 'Pull quote',
    category: 'Social proof',
    description: 'One standout quote, large and centred.',
    keywords: ['quote', 'testimonial', 'review'],
    tree: section({ spacing: 'lg' }, [node('dcms.quote', { text: 'The best decision we made this year.', cite: 'A very happy customer', style: 'large', align: 'center' })]),
  },
  {
    id: 'stats',
    label: 'Numbers',
    category: 'Social proof',
    description: 'Four big numbers with what they count — years, customers, projects.',
    keywords: ['stats', 'figures', 'facts', 'counters'],
    tree: section({ background: 'inverse' }, [
      grid('4', [['12', 'years of experience'], ['3,400', 'happy customers'], ['98%', 'would recommend us'], ['24 h', 'to a reply']].map(([n, l]) => centred(h(n!, '2', { align: 'center' }), p(l!, { align: 'center' })))),
    ]),
  },
  {
    id: 'logos',
    label: 'Logo strip',
    category: 'Social proof',
    description: 'A row of client or partner logos.',
    keywords: ['clients', 'partners', 'trusted by', 'brands'],
    tree: section({ spacing: 'sm' }, [
      p('Trusted by teams at', { align: 'center', tone: 'muted', size: 'sm' }),
      grid('5', [1, 2, 3, 4, 5].map((n) => img(`Logo ${n}`, { ratio: 'auto', fit: 'contain', radius: 'none' })), { gap: 'md' }),
    ]),
  },

  // People
  {
    id: 'team',
    label: 'Team',
    category: 'People',
    description: 'Photos, names and roles of the people behind it.',
    keywords: ['about', 'staff', 'people', 'who we are'],
    tree: section({}, [
      intro('Team', 'The people you’ll talk to', 'Small team, short answers.'),
      grid('4', ['Anna Nováková|Founder', 'Petr Svoboda|Design', 'Lucie Dvořák|Development', 'Martin Černý|Support'].map((x) => {
        const [name, role] = x.split('|');
        return stack({ gap: 'xs', align: 'center' }, [img(name!, { ratio: '1-1', radius: 'lg' }), h(name!, '4', { align: 'center' }), p(role!, { align: 'center', tone: 'muted', size: 'sm' })]);
      })),
    ]),
  },

  // Process
  {
    id: 'steps',
    label: 'Steps',
    category: 'Process',
    description: 'How it works in three numbered steps.',
    keywords: ['how it works', 'process', 'numbered'],
    tree: section({}, [
      intro('How it works', 'Three steps to get going', 'No meetings needed to start.'),
      grid('3', [['1', 'Tell us', 'A short form, two minutes.'], ['2', 'We plan', 'A proposal within two days.'], ['3', 'You decide', 'Go ahead, change it, or walk away.']].map(([n, t, d]) =>
        card({ variant: 'filled' }, [badge(`Step ${n}`), h(t!, '3'), p(d!, { tone: 'muted' })]),
      )),
    ]),
  },
  {
    id: 'timeline',
    label: 'Timeline',
    category: 'Process',
    description: 'Milestones down the page, each with a year and a sentence.',
    keywords: ['history', 'milestones', 'story', 'years'],
    tree: section({}, [
      h('Our journey', '2'),
      stack({ gap: 'lg' }, [['2015', 'Founded in a garage, with one client.'], ['2019', 'Moved into our first real office.'], ['2024', 'A team of twelve, and still growing.']].map(([y, t]) =>
        stack({ direction: 'horizontal', gap: 'md', align: 'start' }, [badge(y!, 'neutral'), p(t!)]),
      )),
    ]),
  },
  {
    id: 'faq',
    label: 'FAQ',
    category: 'Process',
    description: 'Common questions that open to show their answers.',
    keywords: ['questions', 'help', 'answers', 'accordion'],
    tree: section({}, [
      node('dcms.container', { width: 'narrow' }, {
        default: [
          h('Questions people ask', '2', { align: 'center' }),
          node('dcms.accordion', { look: 'lines' }, {
            items: [
              ['How long does it take?', 'Most projects are ready in two to four weeks.'],
              ['What does it cost?', 'We send a fixed price before we start — no surprises.'],
              ['Can I change my mind?', 'Yes, at any point before we begin.'],
              ['Do you work with small businesses?', 'Most of our clients are small businesses.'],
            ].map(([q, a]) => node('dcms.accordion-item', { title: q }, { default: [p(a!)] })),
          }),
        ],
      }),
    ]),
  },

  // Calls to action & pricing
  {
    id: 'cta-band',
    label: 'Call to action',
    category: 'Calls to action',
    description: 'A coloured band asking for the one thing you want visitors to do.',
    keywords: ['cta', 'banner', 'sign up', 'contact'],
    tree: section({ background: 'inverse', spacing: 'lg' }, [
      stack({ align: 'center', gap: 'md' }, [h('Ready when you are', '2', { align: 'center' }), p('Tell us what you need — we answer within a day.', { align: 'center' }), btn('Get in touch')]),
    ]),
  },
  {
    id: 'newsletter',
    label: 'Newsletter',
    category: 'Calls to action',
    description: 'An email sign-up with a sentence on what people will get.',
    keywords: ['email', 'subscribe', 'sign up', 'mailing list'],
    tree: section({ background: 'soft' }, [
      split('1-1', [stack({ gap: 'sm', justify: 'center' }, [h('News, now and then', '2'), p('One email a month with what’s new. No spam, unsubscribe any time.', { tone: 'muted' })])], [
        node('dcms.form', { form: 'newsletter', submitLabel: 'Subscribe', successMessage: 'Thanks — check your inbox to confirm.' }, {
          fields: [node('dcms.field', { name: 'email', label: 'Email address', type: 'email', required: true, placeholder: 'you@example.com' })],
        }),
      ]),
    ]),
  },
  {
    id: 'pricing',
    label: 'Pricing',
    category: 'Pricing',
    description: 'Three plans side by side, the middle one highlighted.',
    keywords: ['plans', 'prices', 'tiers', 'packages', 'cost'],
    tree: section({}, [
      intro('Pricing', 'Simple, honest prices', 'Start small and change plans whenever you like.'),
      grid('3', [
        ['Starter', '€9', 'For trying it out', 'One project\nEmail support', 'secondary', 'outline', null],
        ['Business', '€29', 'For growing teams', 'Ten projects\nPriority support\nTeam accounts', 'primary', 'raised', 'Most popular'],
        ['Enterprise', '€99', 'For larger organisations', 'Unlimited projects\nPhone support\nOwn account manager', 'secondary', 'outline', null],
      ].map(([name, price, sub, items, btnVariant, variant, flag]) =>
        card({ variant, padding: 'lg' }, [
          ...(flag ? [badge(flag)] : []),
          h(name!, '3'),
          stack({ direction: 'horizontal', align: 'end', gap: 'xs' }, [h(price!, '2'), p('/ month', { tone: 'muted' })]),
          p(sub!, { tone: 'muted' }),
          node('dcms.list', { items, marker: 'check' }),
        ], [], [btn(`Choose ${name}`, btnVariant!)]),
      )),
    ]),
  },

  // Contact
  {
    id: 'contact',
    label: 'Contact',
    category: 'Contact',
    description: 'A contact form beside your address, phone, email and a map.',
    keywords: ['get in touch', 'address', 'form', 'map', 'phone'],
    tree: section({}, [
      split('1-1', [
        stack({ gap: 'md' }, [h('Get in touch', '2'), p('Write to us, or drop by — we’re happy to help.', { tone: 'muted' }), node('dcms.form', { form: 'contact' })]),
      ], [
        stack({ gap: 'md' }, [
          stack({ direction: 'horizontal', gap: 'sm', align: 'center' }, [icon('map-pin', { shape: 'plain' }), p('Main Street 1, Prague')]),
          stack({ direction: 'horizontal', gap: 'sm', align: 'center' }, [icon('phone', { shape: 'plain' }), p('+420 123 456 789')]),
          stack({ direction: 'horizontal', gap: 'sm', align: 'center' }, [icon('mail', { shape: 'plain' }), p('hello@example.com')]),
          node('dcms.map', { height: 'sm' }),
        ]),
      ]),
    ]),
  },

  // Content from plugins
  {
    id: 'content-list',
    label: 'Content list',
    category: 'From your content',
    description: 'Cards for your plugin content — events, posts, products — repeated for each item. Choose the content after inserting.',
    keywords: ['events', 'blog', 'posts', 'news', 'collection', 'products'],
    tree: section({}, [
      stack({ direction: 'horizontal', justify: 'between', align: 'end' }, [h('Coming up', '2'), btn('See all', 'ghost')]),
      node('dcms.collection', { limit: 6, layout: 'grid' }, {
        item: [card({ variant: 'raised' }, [h('Title', '3'), p('Short description', { tone: 'muted' })], [img('Picture', { ratio: '16-9', radius: 'none' })], [btn('Details', 'ghost', { size: 'sm' })])],
        empty: [p('Nothing here yet — check back soon.', { tone: 'muted' })],
      }),
    ]),
  },

  // Footers
  {
    id: 'footer',
    label: 'Footer',
    category: 'Footers',
    description: 'Your name, the menu, social links and the small print. Put it in the app shell.',
    keywords: ['bottom', 'copyright', 'links'],
    tree: node('dcms.footer'),
  },
];

/** Categories in the order the gallery shows them. */
export const TEMPLATE_CATEGORIES = [...new Set(SECTION_TEMPLATES.map((t) => t.category))];
