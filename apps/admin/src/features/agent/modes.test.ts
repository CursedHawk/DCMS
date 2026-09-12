import { beforeEach, describe, expect, it } from 'vitest';
import { AI_MODES, type AiMode, decide, rememberMode, storedMode, writesIn } from './modes';

/**
 * The whole safety model is one table, so it gets tested as one table. Each cell here is a
 * decision someone will rely on: "it will not publish without asking me" is the claim the Agent
 * row makes, and it has to be true for every dangerous tool rather than for the one that was
 * checked by hand.
 */
describe('decide', () => {
  const expected: Record<AiMode, [read: string, safe: string, dangerous: string]> = {
    read: ['run', 'unavailable', 'unavailable'],
    careful: ['run', 'approve', 'approve'],
    agent: ['run', 'run', 'approve'],
    auto: ['run', 'run', 'run'],
  };

  for (const mode of AI_MODES) {
    it(`decides correctly in ${mode} mode`, () => {
      const [read, safe, dangerous] = expected[mode];
      expect(decide(mode, 'read')).toBe(read);
      expect(decide(mode, 'safe')).toBe(safe);
      expect(decide(mode, 'dangerous')).toBe(dangerous);
    });
  }

  it('never gates a read, in any mode', () => {
    // A tool the caller's permissions do not reach is not offered at all, so anything readable
    // here is something they could read by clicking around the console.
    expect(AI_MODES.every((mode) => decide(mode, 'read') === 'run')).toBe(true);
  });

  it('lets exactly one mode publish or delete unattended', () => {
    expect(AI_MODES.filter((mode) => decide(mode, 'dangerous') === 'run')).toEqual(['auto']);
  });
});

describe('writesIn', () => {
  it('is false only for read', () => {
    expect(AI_MODES.filter((mode) => !writesIn(mode))).toEqual(['read']);
  });
});

describe('the remembered mode', () => {
  beforeEach(() => localStorage.clear());

  it('starts in Agent', () => {
    expect(storedMode()).toBe('agent');
  });

  it.each(['read', 'careful', 'agent'] as const)('remembers %s', (mode) => {
    rememberMode(mode);
    expect(storedMode()).toBe(mode);
  });

  it('refuses to remember Full auto', () => {
    // The one mode that publishes and deletes with nobody watching is a decision for the person
    // sitting there, not something a browser restores a fortnight later.
    rememberMode('auto');
    expect(storedMode()).toBe('agent');
  });

  it('falls back to Agent on a value it does not recognise', () => {
    localStorage.setItem('dcms.ai.mode', 'write');
    expect(storedMode()).toBe('agent');
  });
});
