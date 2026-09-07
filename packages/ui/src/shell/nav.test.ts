import { describe, expect, it } from 'vitest';
import { FileText, Home, Settings, Users } from 'lucide-react';
import { activeItem, isActive, type ShellNavItem } from './nav';

const item = (to: string, extra: Partial<ShellNavItem> = {}): ShellNavItem => ({
  to,
  label: to,
  icon: Home,
  ...extra,
});

describe('isActive', () => {
  it('matches the item itself', () => {
    expect(isActive(item('/media'), '/media')).toBe(true);
  });

  it('matches a child path, so a detail page keeps its section lit', () => {
    expect(isActive(item('/sites'), '/sites/abc123')).toBe(true);
  });

  it('does NOT match a sibling that merely shares a prefix', () => {
    // A plain startsWith would light "Site" up on "/sitemap", which is the classic bug here.
    expect(isActive(item('/site'), '/sitemap')).toBe(false);
  });

  it('treats "/" as exact, or it matches every page in the app', () => {
    expect(isActive(item('/'), '/')).toBe(true);
    expect(isActive(item('/'), '/media')).toBe(false);
  });

  it('honours an explicit exact flag', () => {
    expect(isActive(item('/content', { exact: true }), '/content/blog')).toBe(false);
  });
});

describe('activeItem', () => {
  const items = [item('/'), item('/settings', { icon: Settings }), item('/settings/members', { icon: Users })];

  it('picks the longest match, so a nested section beats its parent', () => {
    expect(activeItem(items, '/settings/members')?.to).toBe('/settings/members');
  });

  it('falls back to the parent when no child matches', () => {
    expect(activeItem(items, '/settings/domains')?.to).toBe('/settings');
  });

  it('returns nothing for a path no item covers', () => {
    // Real cases: the OIDC callback and the invite-accept page live outside the navigation.
    expect(activeItem(items, '/auth/callback')).toBeUndefined();
  });

  it('does not confuse an unrelated route for the index', () => {
    expect(activeItem([item('/'), item('/forms', { icon: FileText })], '/forms')?.to).toBe('/forms');
  });
});
