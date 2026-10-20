import { describe, expect, it } from 'vitest';
import { paramsFor, withCacheHints } from './modelRouter';

describe('paramsFor', () => {
  it('spends little on a trivial task, without disabling thinking', () => {
    // Every turn used to ask for adaptive thinking at high effort, including "change the button
    // text" — the default that makes an agent feel expensive on the work that should feel instant.
    // Disabled thinking is a 400 on Opus 5.5 / Sonnet 5.5, so effort is the lever.
    expect(paramsFor('trivial')).toMatchObject({
      thinking: { type: 'adaptive' },
      output_config: { effort: 'low' },
    });
  });

  it('scales effort with complexity', () => {
    expect(paramsFor('normal').output_config?.effort).toBe('medium');
    expect(paramsFor('complex').output_config?.effort).toBe('high');
  });

  it('gives a bigger ceiling to harder work', () => {
    expect(paramsFor('trivial').max_tokens).toBeLessThan(paramsFor('normal').max_tokens);
    expect(paramsFor('normal').max_tokens).toBeLessThanOrEqual(paramsFor('complex').max_tokens);
  });

  it('raises effort while repairing, whatever the original classification', () => {
    // A run that has already failed its build has demonstrated the cheap setting was not
    // enough; spending more on the second attempt is cheaper than needing a third.
    expect(paramsFor('trivial', { repairing: true })).toMatchObject({
      thinking: { type: 'adaptive' },
      output_config: { effort: 'high' },
    });
  });
});

describe('withCacheHints', () => {
  const body = {
    messages: [{ role: 'user', content: 'hi' }],
    system: 'you are an agent',
    tools: [
      { name: 'a', description: 'a', input_schema: {} },
      { name: 'b', description: 'b', input_schema: {} },
    ],
    max_tokens: 100,
  };

  it('caches the whole conversation, not just the tools', () => {
    // Top-level cache_control moves the breakpoint to the last block of every request.
    expect(withCacheHints(body).cache_control).toEqual({ type: 'ephemeral' });
    expect((withCacheHints(body).tools as { cache_control?: unknown }[]).some((t) => t.cache_control)).toBe(false);
  });

  it('keeps tools + system cached when pruning rewrites the history', () => {
    expect(withCacheHints(body).system).toEqual([
      { type: 'text', text: 'you are an agent', cache_control: { type: 'ephemeral' } },
    ]);
  });

  it('preserves every other field', () => {
    expect(withCacheHints(body)).toMatchObject({ max_tokens: 100, messages: body.messages, tools: body.tools });
  });

  it('does not mutate the caller’s body', () => {
    const snapshot = JSON.stringify(body);
    withCacheHints(body);
    expect(JSON.stringify(body)).toBe(snapshot);
  });
});
