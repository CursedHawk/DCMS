import { describe, expect, it } from 'vitest';
import { can } from './permissions';

describe('can', () => {
  it('grants a permission the caller actually holds', () => {
    expect(can({ isSuperAdmin: false, permissions: ['media:read'] }, 'media:read')).toBe(true);
  });

  it('refuses one they do not', () => {
    expect(can({ isSuperAdmin: false, permissions: ['media:read'] }, 'media:write')).toBe(false);
  });

  it('short-circuits for a SuperAdmin, mirroring PermissionAuthorizationHandler', () => {
    // If this ever stops matching the server, the UI offers buttons the API refuses.
    expect(can({ isSuperAdmin: true, permissions: [] }, 'anything:at:all')).toBe(true);
  });

  it('refuses everything before the permissions have loaded', () => {
    // The undefined case is the first paint. Defaulting open would flash controls
    // that then disappear, which reads as a bug and briefly invites a 403.
    expect(can(undefined, 'media:read')).toBe(false);
  });

  it('does not match a prefix — permission keys are exact', () => {
    expect(can({ isSuperAdmin: false, permissions: ['media:read'] }, 'media')).toBe(false);
    expect(can({ isSuperAdmin: false, permissions: ['media'] }, 'media:read')).toBe(false);
  });
});
