import { describe, expect, it, vi, afterEach } from 'vitest';
import { bytes, count, dateTime, meterState, ofLimit, plural, relativeTime } from './format';

describe('bytes', () => {
  it('picks the largest unit that leaves a value above 1', () => {
    expect(bytes(999)).toBe('999 B');
    expect(bytes(1024)).toBe('1.0 KB');
    expect(bytes(1024 ** 3 * 2.5)).toBe('2.5 GB');
  });

  it('never renders a fraction of a byte — a partial byte is not a thing', () => {
    expect(bytes(512)).toBe('512 B');
  });

  it('clamps at the largest known unit rather than inventing one', () => {
    expect(bytes(1024 ** 6)).toMatch(/TB$/);
  });

  it('treats nothing, negative and NaN alike as zero', () => {
    expect(bytes(0)).toBe('0 B');
    expect(bytes(-1)).toBe('0 B');
    expect(bytes(Number.NaN)).toBe('0 B');
  });
});

describe('ofLimit', () => {
  it('renders both halves in the LIMIT’s unit, so the ratio is readable', () => {
    // 512 MB of an 8 GB ceiling. Scaling each half independently would read "512 MB / 8 GB",
    // which is the comparison this function exists to avoid making the reader do.
    expect(ofLimit(1024 ** 2 * 512, 1024 ** 3 * 8)).toBe('0.5 / 8.0 GB');
  });

  it('falls back to a bare size when there is no ceiling', () => {
    expect(ofLimit(2048, 0)).toBe('2.0 KB');
  });
});

describe('plural', () => {
  it('uses the singular for exactly one', () => {
    expect(plural(1, 'tenant', undefined, 'en')).toBe('1 tenant');
  });

  it('uses the plural for zero, which is the case a naive template gets wrong', () => {
    expect(plural(0, 'tenant', undefined, 'en')).toBe('0 tenants');
    expect(plural(2, 'tenant', undefined, 'en')).toBe('2 tenants');
  });

  it('takes an irregular plural', () => {
    expect(plural(3, 'entry', 'entries', 'en')).toBe('3 entries');
  });
});

describe('count', () => {
  it('groups digits for the reader’s locale', () => {
    expect(count(1234567, 'en')).toBe('1,234,567');
  });
});

describe('relativeTime', () => {
  afterEach(() => vi.useRealTimers());

  const at = (iso: string) => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(iso));
  };

  it('reads in the past for a past instant', () => {
    at('2026-09-07T12:00:00Z');
    expect(relativeTime('2026-09-07T10:00:00Z', 'en')).toBe('2 hours ago');
  });

  it('reads in the future for a future instant', () => {
    at('2026-09-07T12:00:00Z');
    expect(relativeTime('2026-09-10T12:00:00Z', 'en')).toBe('in 3 days');
  });

  it('uses the largest unit that fits, not seconds for everything', () => {
    at('2026-09-07T12:00:00Z');
    expect(relativeTime('2025-09-07T12:00:00Z', 'en')).toBe('last year');
  });

  it('falls back to seconds under a minute rather than returning nothing', () => {
    at('2026-09-07T12:00:00Z');
    expect(relativeTime('2026-09-07T11:59:58Z', 'en')).toBe('2 seconds ago');
  });

  it('returns the caller’s fallback for missing or unparseable input', () => {
    expect(relativeTime(null, 'en', 'never')).toBe('never');
    expect(relativeTime(undefined, 'en', 'never')).toBe('never');
    expect(relativeTime('not a date', 'en', 'never')).toBe('never');
  });

  it('localises', () => {
    at('2026-09-07T12:00:00Z');
    expect(relativeTime('2026-09-07T10:00:00Z', 'cs')).toContain('hodinami');
  });
});

describe('dateTime', () => {
  it('renders an em dash for nothing, so a table cell is never blank', () => {
    expect(dateTime(null)).toBe('—');
    expect(dateTime('not a date')).toBe('—');
  });

  it('formats a real instant', () => {
    expect(dateTime('2026-09-07T12:00:00Z', 'en-GB')).toMatch(/2026/);
  });
});

describe('meterState', () => {
  it('agrees with the thresholds the platform alerts use', () => {
    expect(meterState(0.79)).toBe('nominal');
    expect(meterState(0.8)).toBe('watch');
    expect(meterState(0.89)).toBe('watch');
    expect(meterState(0.9)).toBe('critical');
    expect(meterState(1.2)).toBe('critical');
  });
});
