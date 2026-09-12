import { render, screen } from '@testing-library/react';
import { act } from 'react';
import { beforeEach, describe, expect, it } from 'vitest';
import { hubConnected, resetHubPresence, setHubConnected, useHubConnected } from './hubPresence';

function Probe({ hub }: { hub: string }) {
  const connected = useHubConnected(hub);
  return <span data-testid="state">{connected === undefined ? 'unknown' : String(connected)}</span>;
}

const state = () => screen.getByTestId('state').textContent;

beforeEach(() => resetHubPresence());

describe('hubPresence', () => {
  it('reports unknown for a hub nobody has opened', () => {
    // Distinct from `false` on purpose: "this app mounts no such hub" and "the socket is down"
    // call for different behaviour, and collapsing them makes every app without one look
    // permanently disconnected.
    render(<Probe hub="absent" />);
    expect(state()).toBe('unknown');
    expect(hubConnected('absent')).toBeUndefined();
  });

  it('re-renders a subscriber when a hub changes state', () => {
    setHubConnected('site', true);
    render(<Probe hub="site" />);
    expect(state()).toBe('true');

    act(() => setHubConnected('site', false));
    expect(state()).toBe('false');
  });

  it('keeps hubs independent', () => {
    setHubConnected('site', true);
    setHubConnected('notifications', false);
    expect(hubConnected('site')).toBe(true);
    expect(hubConnected('notifications')).toBe(false);
  });

  it('stops notifying after unmount', () => {
    setHubConnected('site', true);
    const { unmount } = render(<Probe hub="site" />);
    unmount();
    // No listener remains, so this must not throw or attempt a render.
    act(() => setHubConnected('site', false));
    expect(hubConnected('site')).toBe(false);
  });
});
