import { describe, expect, it } from 'vitest';
import { paramsFor, withCacheHints } from './modelRouter';

describe('paramsFor', () => {
  it('spends nothing on thinking for a trivial task', () => {
    // Every turn used to ask for adaptive thinking at high effort, including "change the button
    // text" — the default that makes an agent feel expensive on the work that should feel instant.
    expect(paramsFor('trivial')).toMatchObject({
      thinking: { type: 'disabled' },
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

  it('puts the stable prefix before the volatile tail', () => {
    // A prefix cache keys on exact order, so anything that changes between turns must come
    // after everything that does not.
    const keys = Object.keys(withCacheHints(body));
    expect(keys[0]).toBe('system');
    expect(keys[1]).toBe('tools');
    expect(keys.at(-1)).toBe('messages');
  });

  it('marks the end of the stable prefix on the last tool', () => {
    const tools = withCacheHints(body).tools as { cache_control?: unknown }[];
    expect(tools[0].cache_control).toBeUndefined();
    expect(tools[1].cache_control).toEqual({ type: 'ephemeral' });
  });

  it('preserves every other field', () => {
    expect(withCacheHints(body)).toMatchObject({ max_tokens: 100, system: 'you are an agent' });
  });

  it('leaves a body with no tools alone', () => {
    const toolless = { system: 's', messages: [] };
    expect(withCacheHints(toolless)).toBe(toolless);
  });

  it('does not mutate the caller’s body', () => {
    const snapshot = JSON.stringify(body);
    withCacheHints(body);
    expect(JSON.stringify(body)).toBe(snapshot);
  });
});
