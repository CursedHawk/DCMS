import { beforeEach, describe, expect, it } from 'vitest';
import { paletteItems, readRecent, rememberVisit } from './CommandPalette';
import type { NavResponseItem } from './navApi';

const t = (key: string) => key;
const server: NavResponseItem[] = [
  { to: '/', labelKey: 'nav.dashboard', icon: 'LayoutDashboard', group: 'overview', label: null },
  { to: '/plugins/crm/records', labelKey: null, icon: 'Table', group: 'apps', label: 'Records', detail: 'CRM' },
  { to: '/plugins/hr/records', labelKey: null, icon: 'Table', group: 'apps', label: 'Records', detail: 'HR' },
  { to: '/content?instance=x', labelKey: 'nav.content', icon: 'Newspaper', group: 'manage', label: 'Press room' },
];

describe('paletteItems', () => {
  it('offers only what the member may open, grouped', () => {
    const items = paletteItems({ isSuperAdmin: false, permissions: ['content:read'] }, server, t, 'en');
    const at = (to: string) => items.find((i) => i.to === to);

    expect(at('/content')?.group).toBe('pages');
    expect(at('/media')).toBeUndefined();
    expect(at('/tenants')).toBeUndefined();
    // Settings sections, filtered the same way: the API reference is open to every member.
    expect(at('/settings/api')?.group).toBe('settings');
    expect(at('/settings/members')).toBeUndefined();
    // The server's own-named entries: an app's screens are Apps, a named collection is a page.
    expect(at('/plugins/crm/records')).toMatchObject({ group: 'apps', label: 'Records (CRM)' });
    expect(at('/plugins/hr/records')).toMatchObject({ group: 'apps', label: 'Records (HR)' });
    expect(at('/content?instance=x')).toMatchObject({ group: 'pages', label: 'Press room' });
  });

  it('finds a site or a plugin instance by its name', () => {
    const items = paletteItems({ isSuperAdmin: false, permissions: [] }, [], t, 'en', {
      sites: [{ id: 's1', name: 'Acme shop' }],
      instances: [{ slug: 'press-room', name: 'Press room' }],
    });
    expect(items.find((i) => i.to === '/sites/s1')).toMatchObject({ group: 'sites', label: 'Acme shop' });
    expect(items.find((i) => i.to === '/plugins/press-room')).toMatchObject({ group: 'plugins', label: 'Press room' });
    // And the pages the topbar leads to, which the menu does not list.
    expect(items.some((i) => i.to === '/jobs')).toBe(true);
  });

  it('lists each destination once', () => {
    const items = paletteItems({ isSuperAdmin: true, permissions: [] }, [...server, ...server], t, 'en');
    expect(new Set(items.map((i) => i.to)).size).toBe(items.length);
    expect(items.some((i) => i.to === '/tenants')).toBe(true);
  });
});

describe('recent destinations', () => {
  beforeEach(() => localStorage.clear());

  it('keeps the last five, newest first, without repeats, per workspace', () => {
    for (const to of ['/a', '/b', '/c', '/a', '/d', '/e', '/f']) rememberVisit('acme', to);
    expect(readRecent('acme')).toEqual(['/f', '/e', '/d', '/a', '/c']);
    expect(readRecent('other')).toEqual([]);
  });

  it('survives storage holding something it did not write', () => {
    localStorage.setItem('dcms.palette.recent:acme', '{not json');
    expect(readRecent('acme')).toEqual([]);
    localStorage.setItem('dcms.palette.recent:acme', JSON.stringify(['/a', 3, null]));
    expect(readRecent('acme')).toEqual(['/a']);
  });
});
