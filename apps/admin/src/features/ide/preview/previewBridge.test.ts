import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  attachPreview,
  BRIDGE_SCRIPT,
  clearPreviewMessages,
  previewAttached,
  previewMessages,
  queryPreview,
  resetBridge,
} from './previewBridge';

beforeEach(resetBridge);

/** Deliver a message to the bridge the way the iframe would. */
function fromFrame(data: unknown) {
  window.dispatchEvent(new MessageEvent('message', { data }));
}

describe('attachment', () => {
  it('reports nothing attached by default', () => {
    expect(previewAttached()).toBe(false);
  });

  it('attaches and detaches', () => {
    const off = attachPreview({ postMessage: vi.fn() } as unknown as Window);
    expect(previewAttached()).toBe(true);
    off();
    expect(previewAttached()).toBe(false);
  });

  it('clears buffered messages on attach', () => {
    // A rebuild produces a new document; errors from the previous version of the code would
    // send the agent to fix something it has already fixed.
    attachPreview({ postMessage: vi.fn() } as unknown as Window);
    fromFrame({ __dcms: 'preview', kind: 'error', text: 'old' });
    expect(previewMessages()).toHaveLength(1);

    attachPreview({ postMessage: vi.fn() } as unknown as Window);
    expect(previewMessages()).toHaveLength(0);
  });
});

describe('message capture', () => {
  beforeEach(() => attachPreview({ postMessage: vi.fn() } as unknown as Window));

  it('collects errors, warnings, exceptions and network failures', () => {
    fromFrame({ __dcms: 'preview', kind: 'error', text: 'boom' });
    fromFrame({ __dcms: 'preview', kind: 'warn', text: 'careful' });
    fromFrame({ __dcms: 'preview', kind: 'network', text: '404 /api/x' });
    expect(previewMessages().map((m) => m.kind)).toEqual(['error', 'warn', 'network']);
  });

  it('ignores messages that are not the bridge protocol', () => {
    // The page may talk to other things; only the bridge's own envelope counts.
    fromFrame({ type: 'webpackHotUpdate' });
    fromFrame('a string');
    fromFrame(null);
    expect(previewMessages()).toHaveLength(0);
  });

  it('keeps the newest when a page floods the buffer', () => {
    for (let i = 0; i < 150; i++) {
      fromFrame({ __dcms: 'preview', kind: 'error', text: `e${i}` });
    }
    const messages = previewMessages();
    expect(messages).toHaveLength(100);
    expect(messages.at(-1)?.text).toBe('e149');
  });

  it('can be cleared explicitly', () => {
    fromFrame({ __dcms: 'preview', kind: 'error', text: 'x' });
    clearPreviewMessages();
    expect(previewMessages()).toHaveLength(0);
  });
});

describe('queries', () => {
  it('returns null when nothing is attached', async () => {
    await expect(queryPreview('dom')).resolves.toBeNull();
  });

  it('posts a query and resolves with the reply', async () => {
    const postMessage = vi.fn((msg: { id: number }) => {
      fromFrame({ __dcms: 'reply', id: msg.id, text: '<div>hi</div>' });
    });
    attachPreview({ postMessage } as unknown as Window);

    await expect(queryPreview('dom', '#root')).resolves.toBe('<div>hi</div>');
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ __dcms: 'query', op: 'dom', selector: '#root' }),
      '*',
    );
  });

  it('times out rather than waiting on a page that never answers', async () => {
    // A page stuck in a render loop does not service messages.
    vi.useFakeTimers();
    attachPreview({ postMessage: vi.fn() } as unknown as Window);
    const pending = queryPreview('dom', undefined, 100);
    await vi.advanceTimersByTimeAsync(150);
    await expect(pending).resolves.toBeNull();
    vi.useRealTimers();
  });

  it('matches replies to their own request', async () => {
    const postMessage = vi.fn((msg: { id: number; selector?: string }) => {
      fromFrame({ __dcms: 'reply', id: msg.id, text: `answer:${msg.selector}` });
    });
    attachPreview({ postMessage } as unknown as Window);

    const [a, b] = await Promise.all([queryPreview('dom', '.a'), queryPreview('dom', '.b')]);
    expect(a).toBe('answer:.a');
    expect(b).toBe('answer:.b');
  });
});

describe('injected script', () => {
  it('is syntactically valid JavaScript', () => {
    /*
     * The script ships as a string inside an inline <script>, so a syntax error in it is
     * completely silent — the preview still renders and every preview tool simply never
     * receives anything. Parsing it here is the only cheap way to catch that.
     *
     * `new Function` is a deliberate exception to the usual prohibition, and safe in this one
     * place: BRIDGE_SCRIPT is a module-level string literal with no interpolation and no
     * untrusted input, and this is a test file that never runs in the app. It is used to PARSE,
     * not to execute — the returned function is discarded.
     */
    expect(() => new Function(BRIDGE_SCRIPT)).not.toThrow();
  });

  it('captures the handlers the tools depend on', () => {
    for (const hook of ['console', 'error', 'unhandledrejection', 'fetch', 'message']) {
      expect(BRIDGE_SCRIPT).toContain(hook);
    }
  });

  it('does not forward console.log', () => {
    // A React app in development is chatty; a debug line the author left in is not a signal
    // worth paying for.
    expect(BRIDGE_SCRIPT).not.toMatch(/wrap\('log'/);
  });
});
