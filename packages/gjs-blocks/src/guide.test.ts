import { BIND_TARGETS, TEMPLATE_ATTRS } from '@dcms/gjs-schema';
import { describe, expect, it } from 'vitest';
import { GUIDE_HEADER, renderAiGuide } from './guide';
import { BUILTIN_SPECS } from './specs';

/**
 * The guide is generated so it cannot go stale — which only holds if it is
 * actually derived from the declarations it describes. These check that: a block
 * added to the catalogue, an attribute added to the template vocabulary or a new
 * bind target has to show up in the document with no further work.
 */

const empty = { themeVariables: [], components: [], pages: [] };

describe('the generated AI guide', () => {
  it('marks itself generated', () => {
    expect(renderAiGuide(empty).startsWith(GUIDE_HEADER)).toBe(true);
  });

  it('lists every block in the catalogue by its identity class', () => {
    const md = renderAiGuide(empty);
    for (const spec of BUILTIN_SPECS) {
      if (!spec.identityClass) continue;
      expect(md, `the guide never mentions ${spec.identityClass}`).toContain(spec.identityClass);
    }
  });

  it('documents every template attribute and bind target', () => {
    const md = renderAiGuide(empty);
    for (const attr of TEMPLATE_ATTRS) {
      expect(md, `the guide never mentions ${attr}`).toContain(attr);
    }
    for (const target of BIND_TARGETS) {
      expect(md, `the guide never mentions the ${target} target`).toContain(target);
    }
  });

  it('names this site’s own pages, components and theme variables', () => {
    const md = renderAiGuide({
      siteName: 'Moordoor',
      themeVariables: ['--dcms-color-brand'],
      pages: [{ path: '/events/:slug', title: 'Event', slug: 'event' }],
      components: [
        {
          version: 1,
          name: 'event-card',
          label: 'Event card',
          category: 'custom',
          props: [],
          template: '<div></div>',
          source: { instanceSlug: 'events', contentType: 'event', mode: 'list' },
        },
      ],
    });

    expect(md).toContain('Moordoor');
    expect(md).toContain('var(--dcms-color-brand)');
    expect(md).toContain('custom:event-card');
    expect(md).toContain('/events/:slug');
    // A detail route is the piece that makes a list clickable, so it has to be
    // explained rather than merely listed.
    expect(md).toContain('itemLink');
  });

  it('says what to do when the site has nothing yet', () => {
    const md = renderAiGuide(empty);
    expect(md).toContain('no components of its own yet');
    expect(md).toContain('no theme tokens yet');
  });
});
