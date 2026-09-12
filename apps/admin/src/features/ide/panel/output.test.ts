import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  appendOutput,
  clearOutput,
  outputLines,
  resetOutput,
  subscribeOutput,
} from './output';

/**
 * The workspace log.
 *
 * <p>The behaviour worth testing is the collapsing: a rebuild that fails the same way on every
 * keystroke must not push forty identical lines and scroll the thing that actually changed off
 * the top.</p>
 */

beforeEach(() => resetOutput());

describe('appendOutput', () => {
  it('records a line with its channel and level', () => {
    appendOutput('git', 'pushed to release');
    expect(outputLines()[0]).toMatchObject({
      channel: 'git',
      level: 'info',
      text: 'pushed to release',
    });
  });

  it('ignores empty text', () => {
    appendOutput('workspace', '   ');
    expect(outputLines()).toHaveLength(0);
  });

  it('collapses an immediate repeat into a count', () => {
    appendOutput('preview', 'build failed');
    appendOutput('preview', 'build failed');
    appendOutput('preview', 'build failed');

    expect(outputLines()).toHaveLength(1);
    expect(outputLines()[0].text).toBe('build failed  (×3)');
  });

  it('does not collapse across channels', () => {
    // The same words from two parts of the workspace are two facts.
    appendOutput('preview', 'failed');
    appendOutput('git', 'failed');
    expect(outputLines()).toHaveLength(2);
  });

  it('does not collapse across levels', () => {
    appendOutput('preview', 'failed', 'warn');
    appendOutput('preview', 'failed', 'error');
    expect(outputLines()).toHaveLength(2);
  });

  it('only collapses the newest line, so an alternating pair reads as two', () => {
    appendOutput('preview', 'a');
    appendOutput('preview', 'b');
    appendOutput('preview', 'a');
    expect(outputLines().map((l) => l.text)).toEqual(['a', 'b', 'a']);
  });

  it('keeps counting past a repeat that already has a count', () => {
    for (let i = 0; i < 5; i++) appendOutput('preview', 'same');
    expect(outputLines()[0].text).toBe('same  (×5)');
  });

  it('bounds the buffer', () => {
    for (let i = 0; i < 600; i++) appendOutput('workspace', `line ${i}`);
    expect(outputLines().length).toBeLessThanOrEqual(500);
    // The newest survive, which is the half anybody is scrolling back through.
    expect(outputLines().at(-1)?.text).toBe('line 599');
  });

  it('gives every line a distinct id', () => {
    appendOutput('workspace', 'one');
    appendOutput('workspace', 'two');
    const ids = outputLines().map((l) => l.id);
    expect(new Set(ids).size).toBe(ids.length);
  });
});

describe('subscribers', () => {
  it('are told about an append', () => {
    const listener = vi.fn();
    subscribeOutput(listener);
    appendOutput('workspace', 'something');
    expect(listener).toHaveBeenCalled();
  });

  it('are told about a collapse, because the text changed', () => {
    appendOutput('workspace', 'x');
    const listener = vi.fn();
    subscribeOutput(listener);
    appendOutput('workspace', 'x');
    expect(listener).toHaveBeenCalled();
  });

  it('stop hearing after unsubscribing', () => {
    const listener = vi.fn();
    subscribeOutput(listener)();
    appendOutput('workspace', 'something');
    expect(listener).not.toHaveBeenCalled();
  });

  it('see a new array each time, so useSyncExternalStore re-renders', () => {
    // Mutating in place would leave the snapshot reference identical and React would skip the
    // render — the log would silently stop updating.
    appendOutput('workspace', 'one');
    const first = outputLines();
    appendOutput('workspace', 'two');
    expect(outputLines()).not.toBe(first);
  });
});

describe('clearOutput', () => {
  it('empties the log and tells subscribers', () => {
    appendOutput('workspace', 'something');
    const listener = vi.fn();
    subscribeOutput(listener);
    clearOutput();
    expect(outputLines()).toHaveLength(0);
    expect(listener).toHaveBeenCalled();
  });
});
