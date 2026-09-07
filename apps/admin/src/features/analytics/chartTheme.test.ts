import { describe, expect, it } from 'vitest';
import { trend } from './chartTheme';

describe('trend', () => {
  it('is positive for a rise and negative for a fall', () => {
    expect(trend(110, 100)).toBeCloseTo(0.1);
    expect(trend(90, 100)).toBeCloseTo(-0.1);
  });

  it('is zero for no change', () => {
    expect(trend(100, 100)).toBe(0);
  });

  it('shows nothing when there is no previous period', () => {
    // A comparison that was never fetched is not a trend of zero.
    expect(trend(100, undefined)).toBeNull();
  });

  it('shows nothing when the previous period was empty', () => {
    // Going from no visitors to forty is not "+0%", and it is not "+∞%" either. It is a first
    // period, and a number here would read as precise when it is meaningless.
    expect(trend(40, 0)).toBeNull();
  });

  it('reports a fall to zero as -100%', () => {
    expect(trend(0, 50)).toBe(-1);
  });
});
