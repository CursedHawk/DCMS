import { describe, expect, it } from 'vitest';
import { actionSchema } from './actions';
import { appSchema, nodeSchema, pageSchema, walk, type Node } from './document';
import { isInternalPath, isSafeExternalHref } from './ids';
import { pageIdFromPath, pagePath } from './paths';

const heading: Node = { id: 'n2', type: 'dcms.heading', props: { text: 'Welcome', level: 1 } };
const home = {
  schemaVersion: 1,
  id: 'home',
  title: 'Home',
  root: { id: 'n1', type: 'dcms.section', slots: { default: [heading] } },
};

function messages(result: { success: boolean; error?: { issues: { message: string }[] } }): string[] {
  return result.success ? [] : (result.error?.issues ?? []).map((i) => i.message);
}

describe('pageSchema', () => {
  it('accepts a nested page', () => {
    expect(pageSchema.safeParse(home).success).toBe(true);
  });

  it('refuses an unknown key instead of dropping it — a typo must not lose content', () => {
    const typo = { ...home, root: { id: 'n1', type: 'dcms.section', slot: { default: [heading] } } };
    expect(pageSchema.safeParse(typo).success).toBe(false);
  });

  it('refuses a node id used twice anywhere in the tree', () => {
    const dup = { ...home, root: { ...home.root, slots: { default: [heading, { ...heading }] } } };
    expect(messages(pageSchema.safeParse(dup))).toContain('node id “n2” is used more than once');
  });

  it('refuses a document from another schema version', () => {
    expect(pageSchema.safeParse({ ...home, schemaVersion: 2 }).success).toBe(false);
  });

  it('refuses a component type without a namespace', () => {
    expect(nodeSchema.safeParse({ id: 'a', type: 'heading' }).success).toBe(false);
  });

  it('walks every node depth first', () => {
    const page = pageSchema.parse(home);
    expect([...walk(page.root)].map((n) => n.id)).toEqual(['n1', 'n2']);
  });
});

describe('appSchema', () => {
  const app = {
    schemaVersion: 1,
    routes: [
      { id: 'home', path: '/', page: 'home' },
      { id: 'event', path: '/events/:slug', page: 'event' },
    ],
    navigation: { main: [{ label: 'Events', to: '/events' }, { label: 'Docs', to: 'https://example.com' }] },
  };

  it('accepts routes, params and menus', () => {
    expect(appSchema.safeParse(app).success).toBe(true);
  });

  it.each(['events', '/Events', '/events/', '/events//x', '/events/*', '/a b'])('refuses route path %s', (path) => {
    expect(appSchema.safeParse({ ...app, routes: [{ id: 'x', path, page: 'x' }] }).success).toBe(false);
  });

  it('refuses two routes that differ only in a parameter name', () => {
    const clash = { ...app, routes: [...app.routes, { id: 'event2', path: '/events/:id', page: 'event' }] };
    expect(messages(appSchema.safeParse(clash))[0]).toMatch(/can never be reached/);
  });

  it('refuses a duplicate route id', () => {
    const dup = { ...app, routes: [...app.routes, { id: 'home', path: '/about', page: 'about' }] };
    expect(messages(appSchema.safeParse(dup))).toContain('route id “home” is used more than once');
  });

  it('refuses a javascript: menu link', () => {
    const bad = { ...app, navigation: { main: [{ label: 'x', to: 'javascript:alert(1)' }] } };
    expect(appSchema.safeParse(bad).success).toBe(false);
  });
});

describe('actions', () => {
  it.each([
    { type: 'navigate', to: '/contact' },
    { type: 'open-external', href: 'https://example.com', newTab: true },
    { type: 'open-external', href: 'mailto:hi@example.com' },
    { type: 'scroll-to', target: 'n4' },
    { type: 'submit-form', form: 'contact' },
    { type: 'open-modal', modal: 'signup' },
    { type: 'show-toast', message: 'Saved', tone: 'success' },
  ])('accepts $type', (action) => {
    expect(actionSchema.safeParse(action).success).toBe(true);
  });

  it.each([
    { type: 'navigate', to: '//evil.example' },
    { type: 'navigate', to: '/\\evil.example' },
    { type: 'navigate', to: 'https://example.com' },
    { type: 'open-external', href: 'javascript:alert(1)' },
    { type: 'open-external', href: 'data:text/html,<script>1</script>' },
    { type: 'run-script', code: 'x' },
  ])('refuses %o', (action) => {
    expect(actionSchema.safeParse(action).success).toBe(false);
  });
});

describe('links', () => {
  it('tells internal paths from open redirects', () => {
    expect(isInternalPath('/')).toBe(true);
    expect(isInternalPath('/events?x=1#top')).toBe(true);
    expect(isInternalPath('//evil.example')).toBe(false);
    expect(isInternalPath('/\\evil.example')).toBe(false);
    expect(isInternalPath('events')).toBe(false);
  });

  it('allow-lists external protocols', () => {
    expect(isSafeExternalHref('https://example.com')).toBe(true);
    expect(isSafeExternalHref('tel:+420123')).toBe(true);
    expect(isSafeExternalHref('JavaScript:alert(1)')).toBe(false);
    expect(isSafeExternalHref('not a url')).toBe(false);
  });
});

describe('paths', () => {
  it('round-trips a page path', () => {
    expect(pageIdFromPath(pagePath('about-us'))).toBe('about-us');
    expect(pageIdFromPath('dcms/pages/../x.json')).toBeNull();
    expect(pageIdFromPath('pages/home.html')).toBeNull();
  });
});
