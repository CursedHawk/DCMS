import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ThemeProvider, useTheme } from './theme';

/** Lets a test drive `prefers-color-scheme` and the change event Radix-free code listens to. */
function stubMatchMedia(dark: boolean) {
  const listeners = new Set<() => void>();
  window.matchMedia = ((query: string) => ({
    matches: dark,
    media: query,
    onchange: null,
    addEventListener: (_: string, fn: () => void) => void listeners.add(fn),
    removeEventListener: (_: string, fn: () => void) => void listeners.delete(fn),
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
  return {
    goDark() {
      dark = true;
      listeners.forEach((fn) => fn());
    },
    listenerCount: () => listeners.size,
  };
}

function Probe() {
  const { theme, resolved, setTheme, toggle } = useTheme();
  return (
    <div>
      <span data-testid="theme">{theme}</span>
      <span data-testid="resolved">{resolved}</span>
      <button onClick={() => setTheme('dark')}>dark</button>
      <button onClick={() => setTheme('system')}>system</button>
      <button onClick={toggle}>toggle</button>
    </div>
  );
}

beforeEach(() => {
  localStorage.clear();
  document.documentElement.classList.remove('dark');
});
afterEach(() => vi.restoreAllMocks());

describe('ThemeProvider', () => {
  it('defaults to system and resolves it against the OS', () => {
    stubMatchMedia(true);
    render(<ThemeProvider><Probe /></ThemeProvider>);
    expect(screen.getByTestId('theme')).toHaveTextContent('system');
    expect(screen.getByTestId('resolved')).toHaveTextContent('dark');
  });

  it('puts the .dark class on <html>, which is what every token block keys off', () => {
    stubMatchMedia(true);
    render(<ThemeProvider><Probe /></ThemeProvider>);
    expect(document.documentElement).toHaveClass('dark');
  });

  it('restores an explicit choice from storage across a reload', () => {
    stubMatchMedia(true);
    localStorage.setItem('dcms.theme', 'light');
    render(<ThemeProvider><Probe /></ThemeProvider>);
    expect(screen.getByTestId('resolved')).toHaveTextContent('light');
    expect(document.documentElement).not.toHaveClass('dark');
  });

  it('persists a choice so it survives the next visit', async () => {
    stubMatchMedia(false);
    render(<ThemeProvider><Probe /></ThemeProvider>);
    await userEvent.click(screen.getByText('dark'));
    expect(localStorage.getItem('dcms.theme')).toBe('dark');
    expect(document.documentElement).toHaveClass('dark');
  });

  it('follows the OS while in system mode', async () => {
    const mq = stubMatchMedia(false);
    render(<ThemeProvider><Probe /></ThemeProvider>);
    expect(screen.getByTestId('resolved')).toHaveTextContent('light');
    mq.goDark();
    expect(await screen.findByText('dark')).toBeInTheDocument();
    expect(document.documentElement).toHaveClass('dark');
  });

  it('stops following the OS once a choice is made, and unsubscribes', async () => {
    const mq = stubMatchMedia(false);
    render(<ThemeProvider><Probe /></ThemeProvider>);
    expect(mq.listenerCount()).toBe(1);
    await userEvent.click(screen.getByText('dark'));
    expect(mq.listenerCount()).toBe(0);
  });

  it('toggle flips the RESOLVED theme, not the mode', async () => {
    // Toggling out of "system" while the OS is dark has to land on light, or the
    // first click appears to do nothing.
    stubMatchMedia(true);
    render(<ThemeProvider><Probe /></ThemeProvider>);
    await userEvent.click(screen.getByText('toggle'));
    expect(screen.getByTestId('theme')).toHaveTextContent('light');
    expect(document.documentElement).not.toHaveClass('dark');
  });

  it('refuses to be used outside the provider rather than silently defaulting', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    expect(() => render(<Probe />)).toThrow(/ThemeProvider/);
  });
});
