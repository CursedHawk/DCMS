import { act, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useVisual } from '../store';
import { CodeIsland } from './CodeIsland';

const contract = { schemaVersion: 1 as const, name: 'countdown', label: 'Countdown', props: [] };

function frame(): HTMLIFrameElement {
  return document.querySelector('iframe[sandbox]') as HTMLIFrameElement;
}

/** A message as the island would send it — from its own window, or from somewhere else. */
function from(source: Window | null, data: unknown) {
  act(() => {
    window.dispatchEvent(new MessageEvent('message', { data, source }));
  });
}

describe('CodeIsland', () => {
  beforeEach(() => useVisual.getState().setIsland({ srcdoc: '<!doctype html><p>island</p>', building: false, error: null }));

  it('is an opaque-origin frame that draws only once its own frame says it is ready', () => {
    render(<CodeIsland contract={contract} props={{ until: 'Friday' }} fallback={<span>placeholder</span>} />);
    expect(frame().getAttribute('sandbox')).toBe('allow-scripts');
    expect(screen.getByText('placeholder')).toBeInTheDocument();

    const post = vi.spyOn(frame().contentWindow!, 'postMessage');
    // Another window saying "ready" is not this frame.
    from(window, { __dcms: 'island', kind: 'ready' });
    expect(post).not.toHaveBeenCalled();

    from(frame().contentWindow, { __dcms: 'island', kind: 'ready' });
    expect(post).toHaveBeenCalledWith({ __dcms: 'island-draw', name: 'countdown', props: { until: 'Friday' } }, '*');
    expect(screen.queryByText('placeholder')).not.toBeInTheDocument();
  });

  it('takes its height from the island, within reason, and shows a failure', () => {
    render(<CodeIsland contract={contract} props={{}} fallback={<span>placeholder</span>} />);
    from(frame().contentWindow, { __dcms: 'island', kind: 'ready' });
    from(frame().contentWindow, { __dcms: 'island', kind: 'size', height: 1e9 });
    expect(frame().style.height).toBe('4000px');
    from(frame().contentWindow, { __dcms: 'island', kind: 'error', message: 'boom' });
    expect(screen.getByText('Countdown: boom')).toBeInTheDocument();
    expect(screen.getByText('placeholder')).toBeInTheDocument();
  });

  it('is the placeholder until there is an island page at all', () => {
    useVisual.getState().setIsland({ srcdoc: null, building: true, error: null });
    render(<CodeIsland contract={contract} props={{}} fallback={<span>placeholder</span>} />);
    expect(frame()).toBeNull();
    expect(screen.getByText('placeholder')).toBeInTheDocument();
  });
});
