import { afterEach, describe, expect, it, vi } from 'vitest';
import { toDateTimeLocal } from './dateTime';

describe('toDateTimeLocal', () => {
  afterEach(() => vi.unstubAllEnvs());

  it('shows a zoned timestamp (a sync’s, .NET round-trip format) in local time', () => {
    vi.stubEnv('TZ', 'Europe/Prague');
    expect(toDateTimeLocal('2026-10-07T16:00:23.0000000+00:00')).toBe('2026-10-07T18:00:23');
    expect(toDateTimeLocal('2026-01-07T16:00:23Z')).toBe('2026-01-07T17:00:23');
    // Whole minutes stay minutes, as the input writes them.
    expect(toDateTimeLocal('2026-01-07T16:00:00Z')).toBe('2026-01-07T17:00');
  });

  it('passes through what the input wrote itself', () => {
    expect(toDateTimeLocal('2026-10-07T16:00')).toBe('2026-10-07T16:00');
    expect(toDateTimeLocal('2026-10-07T16:00:05')).toBe('2026-10-07T16:00:05');
  });

  it('shows nothing for nothing or nonsense', () => {
    expect(toDateTimeLocal(undefined)).toBe('');
    expect(toDateTimeLocal('')).toBe('');
    expect(toDateTimeLocal('next week')).toBe('');
    expect(toDateTimeLocal(42)).toBe('');
  });
});
