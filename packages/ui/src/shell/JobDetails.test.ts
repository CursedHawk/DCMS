import { describe, expect, it } from 'vitest';
import { duration, summarize } from './JobDetails';

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
