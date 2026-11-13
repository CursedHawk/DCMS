import { describe, expect, it } from 'vitest';
import { AI_PROMPTS, aiPrompt, fillPrompt } from './aiPrompts';

describe('AI prompt catalogue', () => {
  it('uses an override when there is one, and the shipped text when it is blank or absent', () => {
    const shipped = AI_PROMPTS.find((p) => p.key === 'assistant')!.text;
    expect(aiPrompt('assistant', { assistant: 'Custom' })).toBe('Custom');
    expect(aiPrompt('assistant', { assistant: '   ' })).toBe(shipped);
    expect(aiPrompt('assistant', null)).toBe(shipped);
  });

  it('fills placeholders, and drops ones it does not know', () => {
    expect(fillPrompt('Edit{{site}}. {{rules}}{{nope}}', { site: ' ("Acme")', rules: 'Rules.' })).toBe('Edit ("Acme"). Rules.');
  });

  it('declares every placeholder its text uses', () => {
    for (const prompt of AI_PROMPTS) {
      const used = [...prompt.text.matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]);
      for (const name of used) expect(prompt.placeholders, `${prompt.key} uses {{${name}}}`).toContain(name);
    }
  });
});
