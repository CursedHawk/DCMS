import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { act } from 'react';
import { useChangeFlash } from './useChangeFlash';

function Probe({ value }: { value: unknown }) {
  return <span data-testid="flash">{useChangeFlash(value) ? 'on' : 'off'}</span>;
}

const state = () => screen.getByTestId('flash').textContent;

beforeEach(() => {
  vi.useFakeTimers();
  window.matchMedia = ((q: string) => ({
    matches: false,
    media: q,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
});
afterEach(() => vi.useRealTimers());

describe('useChangeFlash', () => {
  it('does not flash on first render', () => {
    // Otherwise every row in a table lights up when a page loads, which is exactly the
    // scattered animation this design removes.
    render(<Probe value="Ready" />);
    expect(state()).toBe('off');
  });

  it('flashes when the value changes', () => {
    const { rerender } = render(<Probe value="Processing" />);
    rerender(<Probe value="Ready" />);
    expect(state()).toBe('on');
  });

  it('stops after the duration', () => {
    const { rerender } = render(<Probe value="Processing" />);
    rerender(<Probe value="Ready" />);
    act(() => void vi.advanceTimersByTime(600));
    expect(state()).toBe('off');
  });

  it('does not flash on a re-render with the same value', () => {
    const { rerender } = render(<Probe value="Ready" />);
    rerender(<Probe value="Ready" />);
    expect(state()).toBe('off');
  });

  it('treats NaN as unchanged — Object.is, not ===', () => {
    const { rerender } = render(<Probe value={Number.NaN} />);
    rerender(<Probe value={Number.NaN} />);
    expect(state()).toBe('off');
  });

  it('stays silent when the reader has asked for less motion', () => {
    // The highlight IS the motion, so there is nothing to degrade to.
    window.matchMedia = ((q: string) => ({
      matches: true,
      media: q,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    const { rerender } = render(<Probe value="Processing" />);
    rerender(<Probe value="Ready" />);
    expect(state()).toBe('off');
  });
});
