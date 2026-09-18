import { describe, expect, it } from 'vitest';
import { previewProxyTarget } from './proxyTarget';

const ORIGIN = 'https://admin.example';
const PREFIX = '/api/admin/sites/abc/preview/';

describe('previewProxyTarget', () => {
  it('accepts a request inside the site preview subtree and returns its path', () => {
    expect(previewProxyTarget(`${PREFIX}api/blog/posts`, PREFIX, ORIGIN)).toBe(
      '/api/admin/sites/abc/preview/api/blog/posts',
    );
  });

  it('preserves the query string', () => {
    expect(previewProxyTarget(`${PREFIX}api/blog/posts?page=2&tag=x`, PREFIX, ORIGIN)).toBe(
      '/api/admin/sites/abc/preview/api/blog/posts?page=2&tag=x',
    );
  });

  it('refuses a dot-segment traversal that would escape the subtree', () => {
    // The confused deputy: this starts with the prefix as a string, but resolves to
    // /api/admin/tenants — which would otherwise be fetched with the admin token.
    expect(previewProxyTarget(`${PREFIX}../../../tenants`, PREFIX, ORIGIN)).toBeNull();
    expect(previewProxyTarget(`${PREFIX}api/../../../members`, PREFIX, ORIGIN)).toBeNull();
  });

  it('refuses a path that merely shares the prefix text but is a different route', () => {
    expect(previewProxyTarget('/api/admin/tenants', PREFIX, ORIGIN)).toBeNull();
    expect(previewProxyTarget('/api/admin/sites/abc/preview-secrets', PREFIX, ORIGIN)).toBeNull();
  });

  it('refuses another site preview subtree', () => {
    expect(previewProxyTarget('/api/admin/sites/other/preview/api/blog/posts', PREFIX, ORIGIN)).toBeNull();
  });

  it('refuses an absolute URL to another origin', () => {
    expect(previewProxyTarget(`https://evil.example${PREFIX}api/x`, PREFIX, ORIGIN)).toBeNull();
  });

  it('refuses a protocol-relative URL to another host', () => {
    // //evil/... resolves against the origin's scheme to https://evil, a different origin.
    expect(previewProxyTarget('//evil.example/api/admin/sites/abc/preview/api/x', PREFIX, ORIGIN)).toBeNull();
  });

  it('refuses input that is not a resolvable URL', () => {
    expect(previewProxyTarget('http://', PREFIX, ORIGIN)).toBeNull();
  });
});
