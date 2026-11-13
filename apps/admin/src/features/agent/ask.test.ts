import { describe, expect, it } from 'vitest';
import { formatAnswers, parseQuestions } from './ask';

describe('ask_user', () => {
  const input = {
    questions: [
      { question: 'Which layout?', header: 'Layout', options: [{ label: 'Cards' }, { label: 'List', description: 'Denser' }] },
      { question: 'Which sections?', multiSelect: true, options: [{ label: 'News' }, { label: 'Events' }, { label: '' }] },
    ],
  };

  it('reads the model\'s questions, dropping empty options', () => {
    const questions = parseQuestions(input);
    expect(questions).toEqual([
      { question: 'Which layout?', header: 'Layout', options: [{ label: 'Cards' }, { label: 'List', description: 'Denser' }], multiSelect: false },
      { question: 'Which sections?', header: undefined, options: [{ label: 'News' }, { label: 'Events' }], multiSelect: true },
    ]);
  });

  it('tells the model what to fix instead of asking a broken question', () => {
    expect(parseQuestions({})).toMatch(/non-empty/);
    expect(parseQuestions({ questions: [{ question: 'Yes?', options: [{ label: 'Yes' }] }] })).toMatch(/two options/);
    expect(parseQuestions({ questions: Array(5).fill(input.questions[0]) })).toMatch(/at most four/);
  });

  it('hands back chosen options and the person\'s own words', () => {
    const questions = parseQuestions(input) as Exclude<ReturnType<typeof parseQuestions>, string>;
    const text = formatAnswers(questions, [{ selected: ['Cards'] }, { selected: ['News'], other: 'Job openings' }]);
    expect(text).toContain('1. Which layout?\n   → Cards');
    expect(text).toContain('2. Which sections?\n   → News; their own answer: "Job openings"');
    expect(formatAnswers(questions, null)).toMatch(/skipped/);
  });
});
