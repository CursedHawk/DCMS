import { describe, expect, it } from 'vitest';
import { duration, isAdminPath, summarize } from './JobDetails';

describe('summarize', () => {
  it('keeps top-level facts, counts lists and leaves nested objects out', () => {
    expect(summarize({ inserted: 1100, dryRun: true, table: 'products', errors: [{ row: 3 }], nested: { a: 1 } })).toEqual([
      ['inserted', '1100'],
      ['dryRun', 'true'],
      ['table', 'products'],
      ['errors', '1'],
    ]);
  });

  it('says nothing for what is not an object', () => {
    expect(summarize(null)).toEqual([]);
    expect(summarize([1, 2])).toEqual([]);
    expect(summarize('done')).toEqual([]);
  });
});

describe('duration', () => {
  it('picks the unit a person would', () => {
    expect(duration(850)).toBe('850 ms');
    expect(duration(42_000)).toBe('42 s');
    expect(duration(185_000)).toBe('3 min 5 s');
    expect(duration(7_440_000)).toBe('2 h 4 min');
  });
});

describe('isAdminPath', () => {
  it('agrees with the server: only a path in this console becomes a link', () => {
    expect(isAdminPath('/plugins/crm/configuration?tab=automation&run=1')).toBe(true);
    for (const bad of ['//evil.example', '/\t/evil.example', '/\n/evil.example', '/\\evil.example', 'https://evil.example', 'javascript:alert(1)', '']) {
      expect(isAdminPath(bad), bad).toBe(false);
    }
  });
});
